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
            if (!await ApiAuth.AuthorizeAsync(db, ctx)) return ApiError(401, "Missing or invalid bearer token.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(404, "Table not found.");
            if (MethodGate(table, ctx) is { } denied) return denied;

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

        // Live changes for one table, as Server-Sent Events.
        app.MapGet("/api/v1/{apiName}/subscribe", async (AppDbContext db, HttpContext ctx, string apiName) =>
        {
            if (!await ApiAuth.AuthorizeAsync(db, ctx)) return ApiError(401, "Missing or invalid bearer token.");
            var table = await db.Tables.FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(404, "Table not found.");
            if (MethodGate(table, ctx) is { } denied) return denied;
            if (table.IsProxy) return ApiError(400, "Proxy tables store nothing locally and emit no changes.");

            return TypedResults.ServerSentEvents(Stream(table.Id, ctx.RequestAborted), "record");
        });

        // Read: single record
        app.MapGet("/api/v1/{apiName}/records/{rid}", async (AppDbContext db, HttpContext ctx, string apiName, string rid) =>
        {
            if (!await ApiAuth.AuthorizeAsync(db, ctx)) return ApiError(401, "Missing or invalid bearer token.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(404, "Table not found.");
            if (MethodGate(table, ctx) is { } denied) return denied;
            var record = await db.Records.FirstOrDefaultAsync(r => r.TableId == table.Id && r.Id == rid);
            if (record == null) return ApiError(404, "Record not found.");
            return Results.Ok(ApiDtos.RecordDto(record, table.Fields));
        });

        // Create: same validation/computation as the embedded form. JSON or multipart/form-data (for file fields).
        app.MapPost("/api/v1/{apiName}/records", async (AppDbContext db, HttpContext ctx, string apiName) =>
        {
            if (!await ApiAuth.AuthorizeAsync(db, ctx)) return ApiError(401, "Missing or invalid bearer token.");
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
            var record = new Record
            {
                TableId = table.Id,
                Id = Ids.NewShortId(12),
                JsonData = obj.ToJsonString(),
                CreatedAt = DateTime.UtcNow
            };
            db.Records.Add(record);
            await db.SaveChangesAsync();
            return Results.Created($"/api/v1/{apiName}/records/{record.Id}", ApiDtos.RecordDto(record, fields));
        });

        // Update: PATCH merges onto the stored record, PUT replaces it. JSON or multipart/form-data.
        app.MapMethods("/api/v1/{apiName}/records/{rid}", new[] { "PATCH", "PUT" },
            async (AppDbContext db, HttpContext ctx, string apiName, string rid) =>
        {
            if (!await ApiAuth.AuthorizeAsync(db, ctx)) return ApiError(401, "Missing or invalid bearer token.");
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

            var (merged, outcome) = await RecordEngine.ApplyUpdateAsync(db, table, fields, record, obj, replace);
            if (outcome.HasErrors)
                return Results.BadRequest(new { errors = outcome.Errors, invalid = outcome.InvalidFields });

            record.JsonData = merged.ToJsonString();
            await db.SaveChangesAsync();
            return Results.Ok(ApiDtos.RecordDto(record, fields));
        });

        // Delete: single record
        app.MapDelete("/api/v1/{apiName}/records/{rid}", async (AppDbContext db, HttpContext ctx, string apiName, string rid) =>
        {
            if (!await ApiAuth.AuthorizeAsync(db, ctx)) return ApiError(401, "Missing or invalid bearer token.");
            var table = await db.Tables.FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(404, "Table not found.");
            if (MethodGate(table, ctx) is { } denied) return denied;
            var record = await db.Records.FirstOrDefaultAsync(r => r.TableId == table.Id && r.Id == rid);
            if (record == null) return ApiError(404, "Record not found.");
            db.Records.Remove(record);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = rid });
        });

        // SPA admin routing, serve the builder UI for any non-API path so deep links like /tables/2 or /tables/2/records render without a server reload.
    }

    // Every error the public API returns speaks one shape, {"errors": [...]}, so a client parses failures the same way across every status code.
    private static async IAsyncEnumerable<object> Stream(
        string tableId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var channel = RecordEvents.Subscribe();
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(token))
            {
                if (e.TableId != tableId) continue;
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
