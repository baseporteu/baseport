using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

// Bearer-authenticated public REST API and its OpenAPI 3.2 document.
public static class PublicApiEndpoints
{
    public static void MapPublicApiEndpoints(this WebApplication app)
    {
        // anonymous on purpose: it only ever describes tables an author deliberately switched on
        app.MapGet("/api/openapi.json", async (AppDbContext db) =>
        {
            var settings = await db.SettingsAsync() ?? new AppSettings();
            if (!settings.OpenApiEnabled) return Results.NotFound();

            // A table can be live at /api/v1 without appearing here: ApiEnabled and ApiDocsEnabled are independent.
            var tables = (await db.Tables.Include(t => t.Fields).ToListAsync())
                .Where(t => t.ApiEnabled && t.ApiDocsEnabled).ToList();
            var spec = new JsonObject
            {
                ["openapi"] = "3.2.0",
                ["info"] = new JsonObject
                {
                    ["title"] = settings.ApiTitle,
                    ["version"] = "0.1.0",
                    ["description"] = settings.ApiDescription
                },
                ["servers"] = new JsonArray(new JsonObject { ["url"] = "/" }),
                ["tags"] = OpenApiSpec.BuildTags(tables),
                ["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }),
                ["paths"] = OpenApiSpec.BuildPaths(tables),
                ["components"] = new JsonObject
                {
                    ["securitySchemes"] = new JsonObject
                    {
                        ["bearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["bearerFormat"] = "opaque" }
                    },
                    ["schemas"] = OpenApiSpec.BuildSchemas(tables)
                }
            };
            // Only when an author actually grouped something: a renderer that honours tag groups hides every tag missing from them.
            if (OpenApiSpec.BuildTagGroups(tables) is { } groups) spec["x-tagGroups"] = groups;
            return Results.Json(spec);
        });

        // Read: list records.
        app.MapGet("/api/v1/{apiName}/records", async (AppDbContext db, HttpContext ctx, string apiName, string? q, string? sort, string? order, int? page, int? pageSize) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(401, "Missing or invalid bearer token.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(404, "Table not found.");
            if (MethodGate(table, ctx) is { } denied) return denied;

            var fields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();
            var sortField = fields.FirstOrDefault(f => f.Name == sort);
            var descending = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

            var relations = await ApiLinks.RelationsAsync(db, fields);
            var (expand, expandError) = ApiLinks.ParseExpand(ctx.Request.Query[ApiLinks.ExpandParameter], relations);
            if (expandError is { } listProblem) return ApiError(400, listProblem);

            var result = await QueryEngine.ListAsync(db, table, Array.Empty<FieldDefinition>(), sortField, descending, q, page ?? 1, pageSize ?? 50,
                accessFields: fields, accessUserId: caller.Id);
            var extras = await ApiLinks.ForRecordsAsync(db, apiName, result.Records, relations, expand, caller.Id);
            return Results.Ok(new
            {
                rows = result.Records.Select(r => ApiDtos.RecordDto(r, fields, extras[r.Id].Links, extras[r.Id].Expanded)),
                result.Page,
                result.PageSize,
                result.Total,
                result.TotalPages,
                result.HasMore,
                result.CountExact,
                links = ApiLinks.PageLinks(ctx.Request, result)
            });
        });

        // Live changes for one table, as Server-Sent Events.
        app.MapGet("/api/v1/{apiName}/subscribe", async (IServiceScopeFactory scopes, HttpContext ctx, string apiName) =>
        {
            // A stream outlives any one scope, so this one covers the handshake only and the stream opens its own per event.
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(401, "Missing or invalid bearer token.");
            var table = await db.Tables.FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(404, "Table not found.");
            if (MethodGate(table, ctx) is { } denied) return denied;
            if (table.IsProxy) return ApiError(400, "Proxy tables store nothing locally and emit no changes.");

            return TypedResults.ServerSentEvents(Stream(scopes, table.Id, caller.Id, ctx.RequestAborted), "record");
        });

        // Read: single record
        app.MapGet("/api/v1/{apiName}/records/{rid}", async (AppDbContext db, HttpContext ctx, string apiName, string rid) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(401, "Missing or invalid bearer token.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(404, "Table not found.");
            if (MethodGate(table, ctx) is { } denied) return denied;
            var record = await db.Records.FirstOrDefaultAsync(r => r.TableId == table.Id && r.Id == rid);
            if (record == null) return ApiError(404, "Record not found.");
            var readFields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();
            if (!await RecordAccess.AllowsAsync(db, table, readFields, Permission.Read, caller.Id, rid))
                return ApiError(403, "This record is not yours to read.");

            var readRelations = await ApiLinks.RelationsAsync(db, readFields);
            var (readExpand, readProblem) = ApiLinks.ParseExpand(ctx.Request.Query[ApiLinks.ExpandParameter], readRelations);
            if (readProblem is { } problem) return ApiError(400, problem);

            var read = await ApiLinks.ForRecordAsync(db, apiName, record, readRelations, readExpand, caller.Id);
            return Results.Ok(ApiDtos.RecordDto(record, readFields, read.Links, read.Expanded));
        });

        // Create: same validation/computation as the embedded form. JSON or multipart/form-data (for file fields).
        app.MapPost("/api/v1/{apiName}/records", async (AppDbContext db, HttpContext ctx, string apiName) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(401, "Missing or invalid bearer token.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(404, "Table not found.");
            if (MethodGate(table, ctx) is { } denied) return denied;
            var fields = table.Fields.ToList();
            var (obj, formErrors) = await MultipartRecord.FromRequestAsync(ctx, fields);
            if (formErrors.Count > 0) return Results.BadRequest(new { errors = formErrors });
            var outcome = await RecordEngine.PrepareAsync(db, table, fields, obj);
            if (outcome.HasErrors)
                return Results.BadRequest(new { errors = outcome.Errors, invalid = outcome.InvalidFields });
            if (table.IsProxy)
                return Results.BadRequest(new { errors = new[] { "Proxy tables forward to a remote API and cannot be written via the REST API." } });
            if (!await RecordAccess.AllowsAsync(db, table, fields, Permission.Create, caller.Id, request: obj))
                return ApiError(403, "This record is not yours to create.");
            var record = new Record
            {
                TableId = table.Id,
                Id = Ids.NewShortId(12),
                JsonData = obj.ToJsonString(),
                CreatedAt = DateTime.UtcNow
            };
            db.Records.Add(record);
            await db.SaveChangesAsync();
            var created = await ApiLinks.ForRecordAsync(db, apiName, record, await ApiLinks.RelationsAsync(db, fields), Array.Empty<ApiLinks.Relation>(), caller.Id);
            return Results.Created(ApiLinks.Self(apiName, record.Id), ApiDtos.RecordDto(record, fields, created.Links));
        });

        // Update: PATCH merges onto the stored record, PUT replaces it. JSON or multipart/form-data.
        app.MapMethods("/api/v1/{apiName}/records/{rid}", new[] { "PATCH", "PUT" },
            async (AppDbContext db, HttpContext ctx, string apiName, string rid) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(401, "Missing or invalid bearer token.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(404, "Table not found.");
            if (MethodGate(table, ctx) is { } denied) return denied;
            if (table.IsProxy) return Results.BadRequest(new { errors = new[] { "Proxy tables store nothing locally and cannot be updated." } });

            var record = await db.Records.FirstOrDefaultAsync(r => r.TableId == table.Id && r.Id == rid);
            if (record == null) return ApiError(404, "Record not found.");

            var fields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();
            var (obj, formErrors) = await MultipartRecord.FromRequestAsync(ctx, fields);
            if (formErrors.Count > 0) return Results.BadRequest(new { errors = formErrors });
            var replace = HttpMethods.IsPut(ctx.Request.Method);

            if (!await RecordAccess.AllowsAsync(db, table, fields, Permission.Update, caller.Id, rid, request: obj))
                return ApiError(403, "This record is not yours to change.");

            var (merged, outcome) = await RecordEngine.ApplyUpdateAsync(db, table, fields, record, obj, replace);
            if (outcome.HasErrors)
                return Results.BadRequest(new { errors = outcome.Errors, invalid = outcome.InvalidFields });

            record.JsonData = merged.ToJsonString();
            await db.SaveChangesAsync();
            var written = await ApiLinks.ForRecordAsync(db, apiName, record, await ApiLinks.RelationsAsync(db, fields), Array.Empty<ApiLinks.Relation>(), caller.Id);
            return Results.Ok(ApiDtos.RecordDto(record, fields, written.Links));
        });

        // Delete: single record
        app.MapDelete("/api/v1/{apiName}/records/{rid}", async (AppDbContext db, HttpContext ctx, string apiName, string rid) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(401, "Missing or invalid bearer token.");
            var table = await db.Tables.FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(404, "Table not found.");
            if (MethodGate(table, ctx) is { } denied) return denied;
            var record = await db.Records.FirstOrDefaultAsync(r => r.TableId == table.Id && r.Id == rid);
            if (record == null) return ApiError(404, "Record not found.");
            if (!await RecordAccess.AllowsAsync(db, table, await db.Fields.Where(f => f.TableId == table.Id).ToListAsync(), Permission.Delete, caller.Id, rid))
                return ApiError(403, "This record is not yours to delete.");
            db.Records.Remove(record);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = rid });
        });

        // SPA admin routing, serve the builder UI for any non-API path so deep links like /tables/2 or /tables/2/records render without a server reload.
    }

    // Every error the public API returns speaks one shape, {"errors": [...]}, so a client parses failures the same way across every status code.
    private static async IAsyncEnumerable<object> Stream(
        IServiceScopeFactory scopes, string tableId, string userId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var channel = RecordEvents.Subscribe();
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(token))
            {
                if (e.TableId != tableId) continue;
                if (!await AllowsEventAsync(scopes, tableId, userId, e, token)) continue;
                yield return new
                {
                    action = e.Action,
                    id = e.RecordId,
                    record = e.Json is null ? null : JsonNode.Parse(e.Json)
                };
            }
        }
        finally
        {
            RecordEvents.Unsubscribe(channel);
        }
    }

    // Table and rule are re-read per event, not snapshotted at subscribe time: a stream can stay open for days, and a rule the author tightened has to bind the connections already running under the old one.
    private static async Task<bool> AllowsEventAsync(
        IServiceScopeFactory scopes, string tableId, string userId, RecordEvent e, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var table = await db.Tables.FirstOrDefaultAsync(t => t.Id == tableId && t.ApiEnabled, token);
        if (table is null) return false;
        if (!RecordAccess.HasRule(table, Permission.Read)) return true;

        // A stream must not leak what a read would have refused, and the row may already be gone, so the rule is evaluated against the event payload.
        var fields = await db.Fields.Where(f => f.TableId == tableId).ToListAsync(token);
        return await RecordAccess.AllowsAsync(db, table, fields, Permission.Read, userId,
            row: e.Json is null ? null : JsonNode.Parse(e.Json) as JsonObject);
    }

    private static IResult ApiError(int status, string message) =>
        Results.Json(new { errors = new[] { message } }, statusCode: status);

    // Refuses a method the author switched off.
    private static IResult? MethodGate(TableDefinition table, HttpContext ctx)
    {
        if (ApiMethods.Allows(table, ctx.Request.Method)) return null;
        // 405 owes the caller the list of what would have worked.
        ctx.Response.Headers.Allow = string.Join(", ", ApiMethods.Parse(table.ApiMethods));
        return ApiError(405, $"{ctx.Request.Method} is not enabled for this endpoint.");
    }
}
