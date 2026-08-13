using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

// Tables, fields, records and the OpenAPI proxy import.
public static class TableEndpoints
{
    public static void MapTableEndpoints(this WebApplication app)
    {
        app.MapGet("/api/_admin/tables", async (AppDbContext db) =>
        {
            var tables = await db.Tables.Include(t => t.Fields).ToListAsync();
            var formCounts = await db.FormConfigs.GroupBy(f => f.TableId).Select(g => new { TableId = g.Key, Count = g.Count() }).ToListAsync();
            var recordCounts = await db.Records.GroupBy(r => r.TableId).Select(g => new { TableId = g.Key, Count = g.Count() }).ToListAsync();
            return Results.Ok(tables.Select(t => ApiDtos.TableDto(
                t,
                formCounts.FirstOrDefault(f => f.TableId == t.Id)?.Count ?? 0,
                recordCounts.FirstOrDefault(r => r.TableId == t.Id)?.Count ?? 0)));
        });

        app.MapPost("/api/_admin/tables", async (AppDbContext db, TableDefinition table) =>
        {
            var names = await db.Tables.Select(t => t.Name).ToListAsync();
            var errs = FieldValidation.ValidateTable(table, names);
            if (errs.Count > 0) return Results.BadRequest(new { errors = errs });
            table.Id = Ids.NewShortId(12);
            table.CreatedAt = table.UpdatedAt = DateTime.UtcNow;

            // A field posted inline arrives without an id, and RecordIndexes names its generated column after that id.
            var position = 0;
            foreach (var field in table.Fields)
            {
                field.Id = Ids.NewShortId(12);
                field.TableId = table.Id;
                field.Position = position++;
            }

            db.Tables.Add(table);
            await db.SaveChangesAsync();
            await RecordIndexes.SyncAsync(db, table);
            return Results.Ok(ApiDtos.TableDto(table));
        });

        // Name, description, and the switch that decides whether this table shows up in the OpenAPI 3.2 document and the /api/v1 routes.
        app.MapPatch("/api/_admin/tables/{publicId}", async (AppDbContext db, string publicId, JsonObject patch) =>
        {
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == publicId);
            if (table == null) return Results.NotFound();

            if (patch["name"] is JsonValue nv && nv.TryGetValue<string>(out var name)) table.Name = name;
            if (patch["description"] is JsonValue dv && dv.TryGetValue<string>(out var desc)) table.Description = desc;
            if (patch["apiName"] is JsonValue anv && anv.TryGetValue<string>(out var apiName)) table.ApiName = apiName;
            if (patch["apiEnabled"] is JsonValue av && av.TryGetValue<bool>(out var api)) table.ApiEnabled = api;
            if (patch["apiDocsEnabled"] is JsonValue adev && adev.TryGetValue<bool>(out var apiDocs)) table.ApiDocsEnabled = apiDocs;

            // How the endpoint documents itself, and what it answers.
            if (patch["apiDisplayName"] is JsonValue dnv && dnv.TryGetValue<string>(out var displayName)) table.ApiDisplayName = displayName;
            if (patch["apiNamespace"] is JsonValue nsv && nsv.TryGetValue<string>(out var ns)) table.ApiNamespace = ns;
            if (patch["apiDocumentation"] is JsonValue docv && docv.TryGetValue<string>(out var documentation)) table.ApiDocumentation = documentation;
            if (patch["apiMethods"] is JsonArray methods)
                table.ApiMethods = ApiMethods.Serialize(methods.Select(m => m?.GetValue<string>() ?? ""));

            // A proxy target is not write-once.
            if (table.IsProxy)
            {
                if (patch["proxyUrl"] is JsonValue pu && pu.TryGetValue<string>(out var url)) table.ProxyUrl = url.Trim();
                if (patch["proxyReadUrl"] is JsonValue pr && pr.TryGetValue<string>(out var readUrl)) table.ProxyReadUrl = readUrl.Trim();
                if (patch["proxyMethod"] is JsonValue pm && pm.TryGetValue<string>(out var method)) table.ProxyMethod = method.Trim().ToUpperInvariant();
                // An empty token means "leave it alone": the UI never receives the current one, so it cannot echo it back to preserve it.
                if (patch["proxyToken"] is JsonValue pt && pt.TryGetValue<string>(out var token) && !string.IsNullOrWhiteSpace(token))
                    table.ProxyToken = token.Trim();
                if (patch["clearProxyToken"] is JsonValue ct && ct.TryGetValue<bool>(out var clear) && clear)
                    table.ProxyToken = "";
            }

            var others = await db.Tables.Where(t => t.Id != publicId).Select(t => t.Name).ToListAsync();
            var errs = FieldValidation.ValidateTable(table, others);
            // The published name identifies the table in every URL and generated client, so two tables cannot share one.
            if (!string.IsNullOrWhiteSpace(table.ApiName) &&
                await db.Tables.AnyAsync(t => t.Id != publicId && t.ApiName == table.ApiName))
                errs.Add($"Another table is already published as '{table.ApiName}'.");
            if (errs.Count > 0) return Results.BadRequest(new { errors = errs });

            table.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ApiDtos.TableDto(table));
        });

        app.MapPost("/api/_admin/tables/{publicId}/fields", async (AppDbContext db, string publicId, FieldDefinition field) =>
        {
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == publicId);
            if (table == null) return Results.NotFound();
            var others = table.Fields.Select(f => f.Name).ToList();
            var errs = FieldValidation.ValidateFieldDefinition(field, others, others, tpid => db.Tables.Any(t => t.Id == tpid));
            if (errs.Count > 0) return Results.BadRequest(new { errors = errs });
            field.TableId = table.Id;
            field.Id = Ids.NewShortId(12);
            field.Position = table.Fields.Count == 0 ? 0 : table.Fields.Max(f => f.Position) + 1;
            db.Fields.Add(field);
            table.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await RecordIndexes.SyncAsync(db, table);
            return Results.Ok(ApiDtos.FieldDto(field));
        });

        // Drag-to-reorder in the builder.
        app.MapPut("/api/_admin/tables/{publicId}/fields/order", async (AppDbContext db, string publicId, JsonArray order) =>
        {
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == publicId);
            if (table == null) return Results.NotFound();

            var wanted = order.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
            var unknown = wanted.Where(pid => table.Fields.All(f => f.Id != pid)).ToList();
            if (unknown.Count > 0) return Results.BadRequest(new { errors = new[] { "The order contains fields that are not on this table." } });

            var position = 0;
            foreach (var pid in wanted)
                table.Fields.First(f => f.Id == pid).Position = position++;
            // Anything the client did not mention keeps its relative order at the end.
            foreach (var f in table.Fields.Where(f => !wanted.Contains(f.Id)).OrderBy(f => f.Position).ThenBy(f => f.Id))
                f.Position = position++;

            table.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ApiDtos.TableDto(table));
        });

        app.MapPatch("/api/_admin/tables/{publicId}/fields/{fpid}", async (AppDbContext db, string publicId, string fpid, JsonObject patch) =>
        {
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == publicId);
            if (table == null) return Results.NotFound();
            var field = table.Fields.FirstOrDefault(f => f.Id == fpid);
            if (field == null) return Results.NotFound();

            if (patch["name"] is JsonValue nv && nv.TryGetValue<string>(out var name)) field.Name = name;
            if (patch["label"] is JsonValue lv && lv.TryGetValue<string>(out var label)) field.Label = label;
            if (patch["helpText"] is JsonValue htv && htv.TryGetValue<string>(out var help)) field.HelpText = help;
            if (patch["dataType"] is JsonValue tv && tv.TryGetValue<string>(out var dt)) field.DataType = dt;
            if (patch["expression"] is JsonValue ev && ev.TryGetValue<string>(out var expr)) field.Expression = expr;
            if (patch["optionsJson"] is JsonValue ov && ov.TryGetValue<string>(out var opts)) field.OptionsJson = opts;
            if (patch["defaultValue"] is JsonValue dvv && dvv.TryGetValue<string>(out var def)) field.DefaultValue = def;
            if (patch["currency"] is JsonValue cv && cv.TryGetValue<string>(out var currency)) field.Currency = currency;
            if (patch["isRequired"] is JsonValue rv && rv.TryGetValue<bool>(out var req)) field.IsRequired = req;
            if (patch["pattern"] is JsonValue pv && pv.TryGetValue<string>(out var pat)) field.Pattern = pat;
            if (patch["isHidden"] is JsonValue hv && hv.TryGetValue<bool>(out var hidden)) field.IsHidden = hidden;
            if (patch["isUnique"] is JsonValue uv && uv.TryGetValue<bool>(out var unique)) field.IsUnique = unique;
            if (patch["isIdentifier"] is JsonValue iv && iv.TryGetValue<bool>(out var ident)) field.IsIdentifier = ident;
            // null clears the bound; an absent key leaves it untouched.
            if (patch.ContainsKey("min")) field.Min = (patch["min"] as JsonValue)?.TryGetValue<double>(out var lo) == true ? lo : null;
            if (patch.ContainsKey("max")) field.Max = (patch["max"] as JsonValue)?.TryGetValue<double>(out var hi) == true ? hi : null;

            var others = table.Fields.Where(f => f.Id != fpid).Select(f => f.Name).ToList();
            var all = table.Fields.Select(f => f.Name).ToList();
            var errs = FieldValidation.ValidateFieldDefinition(field, others, all, tpid => db.Tables.Any(t => t.Id == tpid));
            if (errs.Count > 0) return Results.BadRequest(new { errors = errs });

            table.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await RecordIndexes.SyncAsync(db, table);
            return Results.Ok(ApiDtos.FieldDto(field));
        });

        app.MapDelete("/api/_admin/tables/{publicId}/fields/{fpid}", async (AppDbContext db, string publicId, string fpid) =>
        {
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == publicId);
            if (table == null) return Results.NotFound();
            var field = table.Fields.FirstOrDefault(f => f.Id == fpid);
            if (field == null) return Results.NotFound();

            var all = table.Fields.Select(f => f.Name).ToList();
            var blocked = new List<string>();
            foreach (var other in table.Fields.Where(f => f.Id != fpid && FieldValidation.NormalizeType(f.DataType) is "calculated" or "derived"))
            {
                var r = JsExpr.Validate(other.Expression, all);
                if (r.ReferencedFields.Contains(field.Name)) blocked.Add($"{other.Name} references '{field.Name}'.");
            }
            var forms = await db.FormConfigs.Where(x => x.TableId == table.Id).ToListAsync();
            foreach (var form in forms)
            {
                try
                {
                    var el = JsonSerializer.Deserialize<JsonElement>(form.LayoutJson);
                    JsonElement rows = el.ValueKind == JsonValueKind.Object && el.TryGetProperty("rows", out var r2) ? r2 : el;
                    if (rows.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var row in rows.EnumerateArray())
                        {
                            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("cols", out var cols))
                                foreach (var col in cols.EnumerateArray())
                                {
                                    if (col.ValueKind == JsonValueKind.Object && col.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                                        foreach (var item in items.EnumerateArray())
                                            if (item.ValueKind == JsonValueKind.String && item.GetString() == field.Name)
                                                blocked.Add($"Form '{form.Title}' uses '{field.Name}' in its layout.");
                                }
                        }
                    }
                }
                catch { }
            }
            if (blocked.Count > 0) return Results.Conflict(new { errors = blocked });

            db.Fields.Remove(field);
            table.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await RecordIndexes.DropForAsync(db, new[] { field });
            return Results.Ok(new { deleted = field.Id });
        });

        app.MapDelete("/api/_admin/tables/{publicId}", async (AppDbContext db, string publicId) =>
        {
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == publicId);
            if (table == null) return Results.NotFound();
            var forms = await db.FormConfigs.Where(f => f.TableId == table.Id).ToListAsync();
            var records = await db.Records.Where(r => r.TableId == table.Id).ToListAsync();
            db.FormConfigs.RemoveRange(forms);
            db.Records.RemoveRange(records);
            db.Tables.Remove(table);
            await db.SaveChangesAsync();
            await RecordIndexes.DropForAsync(db, table.Fields);
            return Results.Ok(new { deleted = table.Id });
        });

        // Parse an OpenAPI 3.x document server-side and list its operations so the admin can pick the target endpoint a proxy table forwards to.
        app.MapPost("/api/_admin/proxy/operations", async (HttpClient http, JsonObject body) =>
        {
            var specUrl = body["specUrl"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s.Trim() : "";
            if (!Uri.TryCreate(specUrl, UriKind.Absolute, out var _)) return Results.BadRequest(new { errors = new[] { "A valid spec URL is required." } });
            var (ops, err) = await OpenApiProxy.FetchOperationsAsync(http, specUrl);
            if (err != null) return Results.BadRequest(new { errors = new[] { err } });
            return Results.Ok(new { serverUrl = OpenApiProxy.BaseUrl(specUrl), operations = ops });
        });

        // Import a proxy table: inherit the field schema from the OpenAPI operation, keep server-side validation on our end, but store no data, submissions are forwarded to the remote API with the configured Bearer token.
        app.MapPost("/api/_admin/proxy/create", async (AppDbContext db, HttpClient http, JsonObject body) =>
        {
            var name = body["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n.Trim() : "";
            var specUrl = body["specUrl"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s.Trim() : "";
            var path = body["path"] is JsonValue pv && pv.TryGetValue<string>(out var p) ? p : "";
            var method = body["method"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m.Trim().ToUpperInvariant() : "POST";
            var token = body["token"] is JsonValue tv && tv.TryGetValue<string>(out var tok) ? tok.Trim() : "";
            var pathParams = body["pathParams"] as JsonObject ?? new JsonObject();

            if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { errors = new[] { "Table name is required." } });
            if (!Uri.TryCreate(specUrl, UriKind.Absolute, out var _)) return Results.BadRequest(new { errors = new[] { "A valid spec URL is required." } });

            var (ops, err) = await OpenApiProxy.FetchOperationsAsync(http, specUrl);
            if (err != null) return Results.BadRequest(new { errors = new[] { err } });
            var op = ops.FirstOrDefault(o => o.Path == path && o.Method == method);
            if (op == null) return Results.BadRequest(new { errors = new[] { "Selected operation was not found in the spec." } });

            var baseUrl = OpenApiProxy.BaseUrl(specUrl);
            var fullUrl = baseUrl + OpenApiProxy.SubstitutePath(op, pathParams);

            // The GET on the same path is what proxied lookup and list forms read from, and what field sampling probes.
            var readOp = ops.FirstOrDefault(o => o.Path == path && o.Method == "GET") ?? (method == "GET" ? op : null);
            var readUrl = readOp is null ? "" : baseUrl + OpenApiProxy.SubstitutePath(readOp, pathParams);

            var table = new TableDefinition
            {
                Id = Ids.NewShortId(12),
                Name = name,
                IsProxy = true,
                ProxyUrl = fullUrl,
                ProxyMethod = method,
                ProxyToken = token,
                ProxyReadUrl = readUrl,
                ProxyQueryJson = JsonSerializer.Serialize(readOp?.QueryParams ?? new List<string>()),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Plenty of real specs declare every body as a bare {"type":"object"}, which inherits zero fields and leaves an unusable table.
            var props = op.Props;
            string? sampleError = null;
            var inferredFromSample = false;
            if (props.Count == 0 && !string.IsNullOrEmpty(readUrl))
            {
                (props, sampleError) = await OpenApiProxy.SampleFieldsAsync(http, readUrl, token);
                inferredFromSample = props.Count > 0;
            }
            if (props.Count == 0)
                return Results.BadRequest(new { errors = new[] { sampleError ?? "The spec declares no fields for this operation and no sample record could be read. Import cannot infer a schema." } });

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var position = 0;
            foreach (var prop in props)
            {
                if (!used.Add(prop.Name)) continue;
                table.Fields.Add(new FieldDefinition
                {
                    Id = Ids.NewShortId(12),
                    Name = prop.Name,
                    DataType = OpenApiProxy.MapFieldType(prop),
                    OptionsJson = prop.EnumValues.Count > 0 ? JsonSerializer.Serialize(prop.EnumValues) : "[]",
                    IsRequired = prop.Required,
                    Position = position++
                });
            }
            var names = await db.Tables.Select(t => t.Name).ToListAsync();
            var tableErrors = FieldValidation.ValidateTable(table, names);
            if (tableErrors.Count > 0) return Results.BadRequest(new { errors = tableErrors });

            db.Tables.Add(table);
            await db.SaveChangesAsync();
            return Results.Ok(new
            {
                Table = ApiDtos.TableDto(table),
                InferredFromSample = inferredFromSample,
                FieldCount = table.Fields.Count
            });
        });

        app.MapPost("/api/_admin/validate-expression", async (AppDbContext db, JsonObject body) =>
        {
            if (body["expression"] is not JsonValue ev || !ev.TryGetValue<string>(out var expr))
                return Results.BadRequest(new { valid = false, errors = new[] { "expression is required." }, referencedFields = Array.Empty<string>(), sampleOutput = "" });
            var fields = new List<string>();
            if (body["fieldNames"] is JsonArray arr)
                foreach (var item in arr)
                    if (item is JsonValue fj && fj.TryGetValue<string>(out var name)) fields.Add(name);
            var r = JsExpr.Validate(expr, fields);
            if (!r.Valid)
                return Results.Ok(new { valid = false, errors = r.Errors, referencedFields = r.ReferencedFields, sampleOutput = "" });

            // synthesize a sample output: build type-aware placeholder values for every referenced field, evaluate, and show what the result would look like.
            var sampleOutput = "";
            if (body["tableId"] is JsonValue tv && tv.TryGetValue<string>(out var tpid) && r.ReferencedFields.Count > 0)
            {
                var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == tpid);
                if (table != null)
                {
                    var byName = table.Fields.ToDictionary(f => f.Name, f => f);
                    var samples = new Dictionary<string, JsonNode?>();
                    foreach (var fn in r.ReferencedFields)
                        samples[fn] = byName.TryGetValue(fn, out var f) ? JsExpr.SampleValue(f.DataType, f.OptionsJson) : JsonValue.Create("Sample");
                    try
                    {
                        var val = JsExpr.Evaluate(expr, name => samples.TryGetValue(name, out var v) ? v : null);
                        sampleOutput = JsExpr.FormatSample(val);
                    }
                    catch (Exception) { }
                }
            }
            return Results.Ok(new { valid = true, errors = r.Errors, referencedFields = r.ReferencedFields, sampleOutput });
        });

        app.MapPost("/api/_admin/validate-field", async (AppDbContext db, JsonObject body) =>
        {
            var name = body["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? n : "";
            var dataType = body["dataType"] is JsonValue dv && dv.TryGetValue<string>(out var d) ? d : "";
            var expression = body["expression"] is JsonValue ev2 && ev2.TryGetValue<string>(out var e) ? e : "";
            var optionsJson = body["optionsJson"] is JsonValue ov && ov.TryGetValue<string>(out var o) ? o : "[]";
            var isRequired = body["isRequired"] is JsonValue bv && bv.TryGetValue<bool>(out var b) && b;
            var pattern = body["pattern"] is JsonValue pv && pv.TryGetValue<string>(out var pat) ? pat : "";
            var isHidden = body["isHidden"] is JsonValue hv && hv.TryGetValue<bool>(out var h) && h;
            var fieldId = body["fieldId"] is JsonValue fv && fv.TryGetValue<string>(out var fid) ? fid : null;
            var tableId = body["tableId"] is JsonValue tv && tv.TryGetValue<string>(out var tpid) ? tpid : "";

            if (string.IsNullOrWhiteSpace(tableId)) return Results.BadRequest(new { valid = false, errors = new[] { "A table must be selected to validate a field." } });
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == tableId);
            if (table == null) return Results.NotFound();

            var allNames = table.Fields.Select(f => f.Name).ToList();
            var others = table.Fields
                .Where(f => fieldId == null || string.IsNullOrEmpty(fieldId) || f.Id != fieldId)
                .Select(f => f.Name)
                .ToList();

            var field = new FieldDefinition { Name = name, DataType = dataType, Expression = expression, OptionsJson = optionsJson, IsRequired = isRequired, Pattern = pattern, IsHidden = isHidden };
            var errs = FieldValidation.ValidateFieldDefinition(field, others, allNames, tpid => db.Tables.Any(t => t.Id == tpid));
            if (errs.Count > 0) return Results.Ok(new { valid = false, errors = errs, dataType = field.DataType });

            var referenced = Array.Empty<string>();
            if (field.DataType is "calculated" or "derived")
            {
                var vr = JsExpr.Validate(field.Expression, allNames);
                referenced = vr.ReferencedFields.ToArray();
            }
            return Results.Ok(new { valid = true, errors = Array.Empty<string>(), dataType = field.DataType, referencedFields = referenced });
        });

        // One paged, searchable, sortable grid endpoint.
        app.MapGet("/api/_admin/tables/{publicId}/records", async (AppDbContext db, string publicId, string? q, string? sort, string? order, int? page, int? pageSize) =>
        {
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == publicId);
            if (table == null) return Results.NotFound();

            var fields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();
            var sortField = fields.FirstOrDefault(f => f.Name == sort);
            var descending = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

            var result = await QueryEngine.ListAsync(db, table, Array.Empty<FieldDefinition>(), sortField, descending, q, page ?? 1, pageSize ?? 50);
            return Results.Ok(new
            {
                rows = result.Records.Select(r => ApiDtos.RecordDto(r, fields)),
                result.Page,
                result.PageSize,
                result.Total,
                result.TotalPages,
                result.HasMore,
                result.CountExact
            });
        });

        // Same write path as the public API's create (RecordEngine.PrepareAsync), just without a bearer token. JSON or multipart/form-data.
        app.MapPost("/api/_admin/tables/{publicId}/records", async (AppDbContext db, HttpContext ctx, string publicId) =>
        {
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == publicId);
            if (table == null) return Results.NotFound();
            if (table.IsProxy) return Results.BadRequest(new { errors = new[] { "Proxy tables store nothing locally and cannot be written to." } });

            var fields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();
            var (body, formErrors) = await MultipartRecord.FromRequestAsync(ctx, fields);
            if (formErrors.Count > 0) return Results.BadRequest(new { errors = formErrors });
            var outcome = await RecordEngine.PrepareAsync(db, table, fields, body);
            if (outcome.HasErrors) return Results.BadRequest(new { errors = outcome.Errors, invalid = outcome.InvalidFields });

            var record = new Record
            {
                TableId = table.Id,
                Id = Ids.NewShortId(12),
                JsonData = body.ToJsonString(),
                CreatedAt = DateTime.UtcNow
            };
            db.Records.Add(record);
            await db.SaveChangesAsync();
            return Results.Created($"/api/_admin/tables/{publicId}/records/{record.Id}", ApiDtos.RecordDto(record, fields));
        });

        // Editing goes through the same write path as creation, so a rule can never hold on insert and lapse on update. JSON or multipart/form-data.
        app.MapPatch("/api/_admin/tables/{publicId}/records/{rid}", async (AppDbContext db, HttpContext ctx, string publicId, string rid) =>
        {
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == publicId);
            if (table == null) return Results.NotFound();
            if (table.IsProxy) return Results.BadRequest(new { errors = new[] { "Proxy tables store nothing locally, so there is no record to edit." } });

            var record = await db.Records.FirstOrDefaultAsync(r => r.Id == rid && r.TableId == table.Id);
            if (record == null) return Results.NotFound();

            var fields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();
            var (patch, formErrors) = await MultipartRecord.FromRequestAsync(ctx, fields);
            if (formErrors.Count > 0) return Results.BadRequest(new { errors = formErrors });
            var (merged, outcome) = await RecordEngine.ApplyUpdateAsync(db, table, fields, record, patch, replace: false);
            if (outcome.HasErrors) return Results.BadRequest(new { errors = outcome.Errors });

            record.JsonData = merged.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Ok(ApiDtos.RecordDto(record, fields));
        });

        app.MapDelete("/api/_admin/tables/{publicId}/records/{rid}", async (AppDbContext db, string publicId, string rid) =>
        {
            var table = await db.Tables.FirstOrDefaultAsync(t => t.Id == publicId);
            if (table == null) return Results.NotFound();
            var record = await db.Records.FirstOrDefaultAsync(r => r.Id == rid && r.TableId == table.Id);
            if (record == null) return Results.NotFound();
            db.Records.Remove(record);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = record.Id });
        });
    }
}
