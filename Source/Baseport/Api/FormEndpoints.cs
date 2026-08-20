using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

// Forms are a first-class resource, not a sub-resource of a table: they live at /api/forms/{publicId} so the admin UI can manage them from their own page, and so a form can later point somewhere other than a single table.
public static class FormEndpoints
{
    public static void MapFormEndpoints(this WebApplication app)
    {
        app.MapGet("/api/_admin/forms", async (AppDbContext db, string? table, string? kind) =>
        {
            var tables = await db.Tables.ToDictionaryAsync(t => t.Id);
            var query = db.FormConfigs.AsQueryable();

            if (!string.IsNullOrWhiteSpace(table))
            {
                var owner = tables.Values.FirstOrDefault(t => t.Id == table);
                if (owner == null) return Results.NotFound();
                query = query.Where(f => f.TableId == owner.Id);
            }
            if (!string.IsNullOrWhiteSpace(kind))
                query = query.Where(f => f.Kind == FormKinds.Normalize(kind));

            var forms = await query.OrderByDescending(f => f.Id).ToListAsync();
            return Results.Ok(forms.Select(f => ApiDtos.FormDto(f, tables.GetValueOrDefault(f.TableId))));
        });

        app.MapPost("/api/_admin/forms", async (AppDbContext db, JsonObject body) =>
        {
            var tablePid = Str(body, "tableId");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == tablePid);
            if (table == null) return Results.BadRequest(new { errors = new[] { "Select a table for this form." } });

            var form = new FormConfig
            {
                TableId = table.Id,
                Id = Ids.NewShortId(12),
                Kind = FormKinds.Normalize(Str(body, "kind")),
                Actions = FormActions.Serialize(ActionsFrom(body)),
                IsReadOnly = Bool(body, "isReadOnly") ?? false,
                Title = Str(body, "title"),
                Description = Str(body, "description"),
                LayoutJson = Str(body, "layoutJson", "[]"),
                ConfigJson = Str(body, "configJson", "{}"),
                IsPublished = Bool(body, "isPublished") ?? true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var errors = FieldValidation.ValidateForm(form, table.Fields);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            db.FormConfigs.Add(form);
            await db.SaveChangesAsync();
            return Results.Ok(ApiDtos.FormDto(form, table));
        });

        app.MapGet("/api/_admin/forms/{fpid}", async (AppDbContext db, string fpid) =>
        {
            var form = await db.FormConfigs.FirstOrDefaultAsync(f => f.Id == fpid);
            if (form == null) return Results.NotFound();
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == form.TableId);
            return Results.Ok(ApiDtos.FormDto(form, table));
        });

        app.MapPatch("/api/_admin/forms/{fpid}", async (AppDbContext db, string fpid, JsonObject patch) =>
        {
            var form = await db.FormConfigs.FirstOrDefaultAsync(f => f.Id == fpid);
            if (form == null) return Results.NotFound();
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == form.TableId);
            if (table == null) return Results.NotFound();

            // Kind is identity, not configuration: a form renders fields and a list renders rows, and every stored setting belongs to one or the other.
            if (patch.ContainsKey("kind") && FormKinds.Normalize(Str(patch, "kind")) != form.Kind)
                return Results.BadRequest(new { errors = new[] { "A form's kind cannot be changed after it is created. Create a new one instead." } });

            // The bound table is fixed for the same reason: every field name in the layout and config refers to it.
            if (patch["tableId"] is JsonValue tv2 && tv2.TryGetValue<string>(out var newTable)
                && !string.IsNullOrWhiteSpace(newTable) && newTable != table.Id)
                return Results.BadRequest(new { errors = new[] { "A form cannot be moved to another table." } });
            if (patch.ContainsKey("actions")) form.Actions = FormActions.Serialize(ActionsFrom(patch));
            if (Bool(patch, "isReadOnly") is { } readOnly) form.IsReadOnly = readOnly;
            if (patch.ContainsKey("title")) form.Title = Str(patch, "title");
            if (patch.ContainsKey("description")) form.Description = Str(patch, "description");
            if (patch.ContainsKey("layoutJson")) form.LayoutJson = Str(patch, "layoutJson", "[]");
            if (patch.ContainsKey("configJson")) form.ConfigJson = Str(patch, "configJson", "{}");
            if (Bool(patch, "isPublished") is { } published) form.IsPublished = published;
            form.UpdatedAt = DateTime.UtcNow;

            var errors = FieldValidation.ValidateForm(form, table.Fields);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            await db.SaveChangesAsync();
            return Results.Ok(ApiDtos.FormDto(form, table));
        });

        app.MapDelete("/api/_admin/forms/{fpid}", async (AppDbContext db, string fpid) =>
        {
            var form = await db.FormConfigs.FirstOrDefaultAsync(f => f.Id == fpid);
            if (form == null) return Results.NotFound();
            db.FormConfigs.Remove(form);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = form.Id });
        });

        app.MapGet("/api/_admin/forms/{fpid}/preview-token", async (AppDbContext db, string fpid) =>
        {
            if (!await db.FormConfigs.AnyAsync(f => f.Id == fpid)) return Results.NotFound();
            return Results.Ok(new { url = $"/preview/{fpid}?token={PreviewAuth.Issue(fpid)}" });
        });

        app.MapGet("/preview/{fpid}", (string fpid, string? token) =>
        {
            if (!PreviewAuth.Verify(fpid, token)) return Results.Unauthorized();
            return Results.Content(
$$"""
<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Embed Preview</title>
</head>
<body style="font-family:system-ui; margin:0; padding:1rem;">
    <script src="/embed.js?id={{fpid}}"></script>
</body>
</html>
""", "text/html");
        });

        // The embed only ever learns what it needs to render: the layout and the fields it may show.
        app.MapGet("/api/forms/{fpid}/schema", async (AppDbContext db, HttpContext ctx, string fpid) =>
        {
            var (form, table, fields) = await LoadAsync(db, fpid);
            if (form is null || table is null) return Results.NotFound();

            // A form that both submits and looks up must expose the union: the fields it collects and the fields it reveals on a hit.
            List<FieldDefinition> visible;
            if (form.Kind == FormKinds.List)
            {
                visible = ListColumns(form, fields);
            }
            else
            {
                var actions = FormActions.Parse(form.Actions);
                visible = new List<FieldDefinition>();
                if (actions.Contains(FormActions.Submit))
                    visible.AddRange(fields.Where(f => !f.IsHidden && FieldValidation.NormalizeType(f.DataType) != "derived"));
                if (actions.Contains(FormActions.Lookup))
                    visible.AddRange(LookupResultFields(form, fields).Where(f => !visible.Contains(f)));
            }
            var currency = (await db.SettingsAsync())?.Currency ?? "EUR";
            return Results.Ok(ApiDtos.PublicFormSchema(form, table, visible, currency));
        }).RequireRateLimiting(RateLimit.Schema);

        // reference dropdown options: id + label only, never the record's own data, and only for a real query, never the whole table
        app.MapGet("/api/forms/{fpid}/reference/{fieldName}", async (AppDbContext db, string fpid, string fieldName, string? q) =>
        {
            var (form, table, fields) = await LoadAsync(db, fpid);
            if (form is null || table is null) return Results.NotFound();

            var field = fields.FirstOrDefault(f => f.Name == fieldName && !f.IsHidden);
            if (field is null || FieldValidation.NormalizeType(field.DataType) != "reference") return Results.NotFound();

            var targetId = FieldValidation.RefTableId(field.OptionsJson);
            if (targetId is null || string.IsNullOrWhiteSpace(q)) return Results.Ok(new { rows = Array.Empty<object>() });

            var records = await db.Records.Where(r => r.TableId == targetId && r.JsonData.Contains(q))
                .OrderByDescending(r => r.Id).Take(20).ToListAsync();
            return Results.Ok(new { rows = records.Select(r => new { r.Id, Label = RecordLabel(r) }) });
        }).RequireRateLimiting(RateLimit.Schema);

        app.MapPost("/api/forms/{fpid}/form", SubmitAsync).RequireRateLimiting(RateLimit.Submit);

        static async Task<IResult> SubmitAsync(AppDbContext db, HttpClient http, HttpContext ctx, string fpid)
        {
            var (form, table, fields) = await LoadAsync(db, fpid);
            if (form is null || table is null) return Results.NotFound();
            if (form.Kind != FormKinds.Form || !FormActions.Parse(form.Actions).Contains(FormActions.Submit))
                return Results.BadRequest(new { errors = new[] { "This form does not accept submissions." } });
            if (form.IsReadOnly)
                return Results.BadRequest(new { errors = new[] { "This form is read-only." } });

            // JSON or multipart/form-data (the latter is how a file field's upload arrives).
            var (obj, formErrors) = await MultipartRecord.FromRequestAsync(ctx, fields);
            if (formErrors.Count > 0) return Results.BadRequest(new { errors = formErrors });

            // Full data-integrity validation, default application, uniqueness, system-id generation and calculated/derived recomputation, shared with the REST API so both paths behave identically.
            var outcome = await RecordEngine.PrepareAsync(db, table, fields, obj);
            if (outcome.HasErrors)
                return Results.BadRequest(new { errors = outcome.Errors, invalid = outcome.InvalidFields });

            if (table.IsProxy)
                return await ForwardAsync(http, table, obj);

            var record = new Record
            {
                TableId = table.Id,
                Id = Ids.NewShortId(12),
                JsonData = obj.ToJsonString(),
                CreatedAt = DateTime.UtcNow
            };
            db.Records.Add(record);
            await db.SaveChangesAsync();
            return Results.Ok(new { success = true, recordId = record.Id });
        }

        // Finds at most one record by any configured identifier field the visitor already knows.
        app.MapGet("/api/forms/{fpid}/form", LookupAsync).RequireRateLimiting(RateLimit.Lookup);

        static async Task<IResult> LookupAsync(AppDbContext db, HttpClient http, HttpContext ctx, string fpid, string? q)
        {
            // A lookup answers "does this identifier exist?", so it is the one endpoint an attacker can turn into an enumeration oracle.
            var (form, table, fields) = await LoadAsync(db, fpid);
            if (form is null || table is null) return Results.NotFound();
            if (form.Kind != FormKinds.Form || !FormActions.Parse(form.Actions).Contains(FormActions.Lookup))
                return Results.BadRequest(new { errors = new[] { "This form does not support lookup." } });

            var config = QueryEngine.ParseConfig(form.ConfigJson);
            var matchFields = QueryEngine.Resolve(fields, config["matchFields"]);
            if (matchFields.Count == 0)
                return Results.BadRequest(new { errors = new[] { "This lookup has no identifier field configured." } });

            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { errors = new[] { "Enter a value to look up." } });

            var notFound = Str(config, "notFoundText", "No matching record was found.");
            var visible = LookupResultFields(form, fields);

            if (table.IsProxy)
            {
                if (!ProxyQuery.CanRead(table))
                    return Results.BadRequest(new { errors = new[] { "This proxy table has no readable GET endpoint." } });

                var (remote, error) = await ProxyQuery.LookupAsync(http, table, matchFields, q);
                if (error != null) return Results.BadRequest(new { errors = new[] { error } });
                if (remote == null) return Results.NotFound(new { found = false, message = notFound });
                return Results.Ok(new { found = true, proxy = true, Data = ProxyQuery.Project(remote, visible) });
            }

            var record = await QueryEngine.LookupAsync(db, table, matchFields, q);
            if (record == null)
                return Results.NotFound(new { found = false, message = notFound });

            return Results.Ok(new
            {
                found = true,
                record.Id,
                record.CreatedAt,
                Data = QueryEngine.Project(record, visible)
            });
        }

        app.MapGet("/api/forms/{fpid}/list", ListAsync).RequireRateLimiting(RateLimit.List);

        static async Task<IResult> ListAsync(AppDbContext db, HttpClient http, HttpContext ctx, string fpid, string? q, int? page, int? pageSize)
        {
            var (form, table, fields) = await LoadAsync(db, fpid);
            if (form is null || table is null) return Results.NotFound();
            if (form.Kind != FormKinds.List)
                return Results.BadRequest(new { errors = new[] { "This form is not a list." } });

            var config = QueryEngine.ParseConfig(form.ConfigJson);
            var columns = ListColumns(form, fields);
            var searchFields = QueryEngine.Resolve(fields, config["searchFields"]);
            if (searchFields.Count == 0) searchFields = columns;

            var sortName = Str(config, "sortField");
            var sortField = fields.FirstOrDefault(f => f.Name == sortName);
            var descending = !string.Equals(Str(config, "sortDir", "desc"), "asc", StringComparison.OrdinalIgnoreCase);

            // The configured page size is the ceiling a visitor may request, so a crafted ?pageSize= can never turn a small list into a full export.
            var filters = QueryEngine.ParseFilters(fields, config["filters"]);
            var configured = (int?)(config["pageSize"] as JsonValue)?.GetValue<double?>() ?? 25;
            var paged = configured > 0;
            var effective = paged ? Math.Min(pageSize ?? configured, configured) : QueryEngine.MaxPageSize;
            var renderers = config["renderers"] as JsonObject ?? new JsonObject();
            var columnDtos = columns.Select(f => new
            {
                f.Name,
                Label = string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label,
                f.DataType,
                f.Currency,
                Render = renderers[f.Name] is JsonValue rv && rv.TryGetValue<string>(out var expr) ? expr : null
            });

            // An action's URL may reference a field that isn't itself a displayed column (e.g. a hidden id) so the row projection is widened to include it without adding it to the header.
            var actions = config["actions"] as JsonArray ?? new JsonArray();
            var actionFieldNames = actions.OfType<JsonObject>()
                .Select(a => (a["hrefExpr"] as JsonValue)?.GetValue<string>())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .SelectMany(e => JsExpr.Validate(e!, fields.Select(f => f.Name).ToList()).ReferencedFields)
                .ToHashSet();
            var projectionFields = actionFieldNames.Count == 0
                ? columns
                : columns.Concat(fields.Where(f => actionFieldNames.Contains(f.Name) && !columns.Contains(f))).ToList();

            if (table.IsProxy)
            {
                if (!ProxyQuery.CanRead(table))
                    return Results.BadRequest(new { errors = new[] { "This proxy table has no readable GET endpoint." } });

                var (remotePage, error) = await ProxyQuery.ListAsync(http, table, searchFields, q, page ?? 1, effective, filters, sortField, descending);
                if (error != null) return Results.BadRequest(new { errors = new[] { error } });
                return Results.Ok(new
                {
                    columns = columnDtos,
                    actions,
                    rows = remotePage!.Records.Select(r => new { Data = ProxyQuery.Project(r, projectionFields) }),
                    proxy = true,
                    Paged = paged,
                    Page = Math.Max(1, page ?? 1),
                    PageSize = effective,
                    remotePage.Total,
                    TotalPages = Math.Max(1, (int)Math.Ceiling(remotePage.Total / (double)effective))
                });
            }

            var result = await QueryEngine.ListAsync(db, table, searchFields, sortField, descending, q, page ?? 1, effective, filters);
            return Results.Ok(new
            {
                columns = columnDtos,
                actions,
                rows = result.Records.Select(r => new { r.Id, r.CreatedAt, Data = QueryEngine.Project(r, projectionFields) }),
                Paged = paged,
                result.Page,
                result.PageSize,
                result.Total,
                result.TotalPages,
                result.HasMore,
                result.CountExact
            });
        }
    }

    // same convention as embed.js's client-side recordLabel: first non-empty scalar value, capped at 40 chars
    private static string RecordLabel(Record r)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.JsonData) ? "{}" : r.JsonData);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var s = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null
                };
                if (!string.IsNullOrEmpty(s)) return s.Length > 40 ? s[..40] : s;
            }
        }
        catch (JsonException) { }
        return "Record";
    }

    // unpublished forms are invisible to every public route
    private static async Task<(FormConfig? Form, TableDefinition? Table, List<FieldDefinition> Fields)> LoadAsync(AppDbContext db, string fpid)
    {
        var form = await db.FormConfigs.FirstOrDefaultAsync(f => f.Id == fpid && f.IsPublished);
        if (form == null) return (null, null, new List<FieldDefinition>());
        var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.Id == form.TableId);
        var fields = table?.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList() ?? new List<FieldDefinition>();
        return (form, table, fields);
    }

    // Fields a lookup result may reveal.
    private static List<FieldDefinition> LookupResultFields(FormConfig form, List<FieldDefinition> fields)
    {
        var chosen = QueryEngine.Resolve(fields, QueryEngine.ParseConfig(form.ConfigJson)["resultFields"]);
        return chosen.Count > 0 ? chosen : fields.Where(f => !f.IsHidden).ToList();
    }

    private static List<FieldDefinition> ListColumns(FormConfig form, List<FieldDefinition> fields)
    {
        var chosen = QueryEngine.Resolve(fields, QueryEngine.ParseConfig(form.ConfigJson)["columns"]);
        return chosen.Count > 0 ? chosen : fields.Where(f => !f.IsHidden).Take(6).ToList();
    }

    // Proxy tables store nothing locally: the validated payload is relayed and the remote verdict returned.
    private static async Task<IResult> ForwardAsync(HttpClient http, TableDefinition table, JsonObject obj)
    {
        if (ProxyTarget.Problem(table.ProxyUrl) is { } blocked)
            return Results.BadRequest(new { errors = new[] { blocked } });

        using var req = new HttpRequestMessage(
            new HttpMethod(string.IsNullOrWhiteSpace(table.ProxyMethod) ? "POST" : table.ProxyMethod),
            table.ProxyUrl);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        if (!string.IsNullOrWhiteSpace(table.ProxyToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", table.ProxyToken);
        req.Content = JsonContent.Create(obj);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req); }
        catch (HttpRequestException ex)
        {
            Serilog.Log.Warning("Proxy write {Table} {Method} {Url} -> {Error}", table.Name, table.ProxyMethod, ProxyLog.Redact(table.ProxyUrl), ex.Message);
            return Results.BadRequest(new { errors = new[] { $"Proxy request failed: {ex.Message}" } });
        }
        catch (TaskCanceledException)
        {
            Serilog.Log.Warning("Proxy write {Table} {Method} {Url} -> timed out", table.Name, table.ProxyMethod, ProxyLog.Redact(table.ProxyUrl));
            return Results.BadRequest(new { errors = new[] { "Proxy request timed out." } });
        }

        Serilog.Log.Information("Proxy write {Table} {Method} {Url} -> {Status} in {Elapsed:0}ms",
            table.Name, table.ProxyMethod, ProxyLog.Redact(table.ProxyUrl), (int)resp.StatusCode,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        var raw = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            var detail = OpenApiProxy.TryParseError(raw);
            var msg = detail != null
                ? $"Remote API rejected the submission ({resp.StatusCode}). {detail}"
                : $"Remote API rejected the submission ({resp.StatusCode}).";
            return Results.BadRequest(new { errors = new[] { msg } });
        }
        return Results.Ok(new { success = true, proxy = true, status = (int)resp.StatusCode, response = OpenApiProxy.TryParseJson(raw) ?? raw });
    }

    // Budgets per client, per form, per minute.


    // Reads the enabled actions, accepting an array or a comma-separated string.
    private static List<string> ActionsFrom(JsonObject body)
    {
        if (body["actions"] is JsonArray arr)
            return FormActions.Parse(string.Join(",", arr.Select(a => a?.GetValue<string>() ?? "")));
        return FormActions.Parse(Str(body, "actions", FormActions.Submit));
    }

    private static string Str(JsonObject o, string key, string fallback = "") =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    private static bool? Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
