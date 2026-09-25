using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

public static class PublicApiEndpoints
{
    public static void MapPublicApiEndpoints(this WebApplication app)
    {

        app.MapGet("/api/openapi.json", async (AppDbContext db) =>
        {
            var settings = await db.SettingsAsync() ?? new AppSettings();
            if (!settings.OpenApiEnabled) return Results.NotFound();

            var version = OpenApiCache.CurrentVersion;
            if (OpenApiCache.Get(version) is { } cached) return Results.Content(cached, "application/json");

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
                ["security"] = new JsonArray(new JsonObject { [OpenApiSpec.SecurityScheme] = new JsonArray() }),
                ["paths"] = OpenApiSpec.BuildPaths(tables),
                ["components"] = new JsonObject
                {
                    ["securitySchemes"] = new JsonObject
                    {
                        [OpenApiSpec.SecurityScheme] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" }
                    },
                    ["schemas"] = OpenApiSpec.BuildSchemas(tables)
                }
            };

            if (OpenApiSpec.BuildTagGroups(tables) is { } groups) spec["x-tagGroups"] = groups;

            var json = spec.ToJsonString();
            OpenApiCache.Set(version, json);
            return Results.Content(json, "application/json");
        });

        app.MapGet("/api/v1/{apiName}/records", async (AppDbContext db, HttpContext ctx, string apiName, string? q, string? sort, string? order, int? page, int? pageSize, string? cursor, string[]? filter) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(ctx, ApiProblem.Unauthorized, "Missing or invalid bearer token.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(ctx, ApiProblem.NotFound, "Table not found.");
            if (MethodGate(table, caller, ctx) is { } denied) return denied;
            if (AcceptGate(ctx) is { } unacceptable) return unacceptable;

            var fields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();
            var sortField = fields.FirstOrDefault(f => f.Name == sort);
            var descending = !string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);

            var (parsedFilters, filterError) = ParseFilterParams(fields, filter);
            if (filterError is { } badFilter) return ApiError(ctx, ApiProblem.BadRequest, badFilter);

            var relations = await ApiLinks.RelationsAsync(db, fields, ctx.RequestAborted);
            var (expand, expandError) = ApiLinks.ParseExpand(ctx.Request.Query[ApiLinks.ExpandParameter], relations);
            if (expandError is { } listProblem) return ApiError(ctx, ApiProblem.BadRequest, listProblem);

            QueryEngine.Cursor? from = null;
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                if (sortField is not null)
                    return ApiError(ctx, ApiProblem.BadRequest, "cursor cannot be combined with sort: a keyset walk is only defined for the default newest-first order. Use page for a sorted listing.");
                from = QueryEngine.Cursor.Decode(cursor);
                if (from is null) return ApiError(ctx, ApiProblem.BadRequest, "cursor is not a cursor this endpoint issued.");
            }

            var result = await QueryEngine.ListAsync(db, table, Array.Empty<FieldDefinition>(), sortField, descending, q, page ?? 1, pageSize ?? 50,
                filters: parsedFilters, accessFields: fields, accessUserId: caller.Id, accessRole: caller.Role, cursor: from);
            var extras = await ApiLinks.ForRecordsAsync(db, apiName, result.Records, relations, expand, caller, ctx.RequestAborted);
            return Results.Ok(new
            {
                rows = result.Records.Select(r => ApiDtos.RecordDto(r, fields, extras[r.Id].Links, extras[r.Id].Expanded)),
                result.Page,
                result.PageSize,
                result.Total,
                result.TotalPages,
                result.HasMore,
                result.CountExact,
                result.NextCursor,
                links = ApiLinks.PageLinks(ctx.Request, result)
            });
        });

        app.MapGet("/api/v1/{apiName}/subscribe", async (IServiceScopeFactory scopes, HttpContext ctx, string apiName) =>
        {

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(ctx, ApiProblem.Unauthorized, "Missing or invalid bearer token.");
            var table = await db.Tables.FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(ctx, ApiProblem.NotFound, "Table not found.");
            if (MethodGate(table, caller, ctx) is { } denied) return denied;
            if (AcceptGate(ctx) is { } unacceptable) return unacceptable;
            if (table.IsProxy) return ApiError(ctx, ApiProblem.BadRequest, "Proxy tables store nothing locally and emit no changes.");

            return TypedResults.ServerSentEvents(Stream(scopes, table.Id, null, caller.Id, caller.Role, ctx.RequestAborted), "record");
        });

        app.MapGet("/api/v1/{apiName}/subscribe/{rid}", async (IServiceScopeFactory scopes, HttpContext ctx, string apiName, string rid) =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(ctx, ApiProblem.Unauthorized, "Missing or invalid bearer token.");
            var table = await db.Tables.FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(ctx, ApiProblem.NotFound, "Table not found.");
            if (MethodGate(table, caller, ctx) is { } denied) return denied;
            if (AcceptGate(ctx) is { } unacceptable) return unacceptable;
            if (table.IsProxy) return ApiError(ctx, ApiProblem.BadRequest, "Proxy tables store nothing locally and emit no changes.");

            if (!await db.Records.AnyAsync(r => r.TableId == table.Id && r.Id == rid)) return ApiError(ctx, ApiProblem.NotFound, "Record not found.");
            var fields = await db.Fields.Where(f => f.TableId == table.Id).ToListAsync();
            if (!await RecordAccess.AllowsAsync(db, table, fields, Permission.Read, caller.Id, rid, callerRole: caller.Role))
                return ApiError(ctx, ApiProblem.Forbidden, "This record is not yours to read.");

            return TypedResults.ServerSentEvents(Stream(scopes, table.Id, rid, caller.Id, caller.Role, ctx.RequestAborted), "record");
        });

        app.MapGet("/api/v1/{apiName}/records/{rid}", async (AppDbContext db, HttpContext ctx, string apiName, string rid) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(ctx, ApiProblem.Unauthorized, "Missing or invalid bearer token.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(ctx, ApiProblem.NotFound, "Table not found.");
            if (MethodGate(table, caller, ctx) is { } denied) return denied;
            if (AcceptGate(ctx) is { } unacceptable) return unacceptable;
            var record = await db.Records.FirstOrDefaultAsync(r => r.TableId == table.Id && r.Id == rid);
            if (record == null) return ApiError(ctx, ApiProblem.NotFound, "Record not found.");
            var readFields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();
            if (!await RecordAccess.AllowsAsync(db, table, readFields, Permission.Read, caller.Id, rid, callerRole: caller.Role))
                return ApiError(ctx, ApiProblem.Forbidden, "This record is not yours to read.");

            var readRelations = await ApiLinks.RelationsAsync(db, readFields, ctx.RequestAborted);
            var (readExpand, readProblem) = ApiLinks.ParseExpand(ctx.Request.Query[ApiLinks.ExpandParameter], readRelations);
            if (readProblem is { } problem) return ApiError(ctx, ApiProblem.BadRequest, problem);

            ApiConditional.SetETag(ctx, record);
            if (ApiConditional.NotModified(ctx, record)) return Results.StatusCode(StatusCodes.Status304NotModified);

            var read = await ApiLinks.ForRecordAsync(db, apiName, record, readRelations, readExpand, caller, ctx.RequestAborted);
            return Results.Ok(ApiDtos.RecordDto(record, readFields, read.Links, read.Expanded));
        });

        app.MapPost("/api/v1/{apiName}/records", async (AppDbContext db, HttpContext ctx, string apiName) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(ctx, ApiProblem.Unauthorized, "Missing or invalid bearer token.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(ctx, ApiProblem.NotFound, "Table not found.");
            if (MethodGate(table, caller, ctx) is { } denied) return denied;
            if (AcceptGate(ctx) is { } unacceptable) return unacceptable;
            if (MediaTypeGate(ctx) is { } unsupported) return unsupported;
            var fields = table.Fields.ToList();
            var (obj, formErrors) = await MultipartRecord.FromRequestAsync(ctx, fields);
            if (formErrors.Count > 0) return ApiProblems.Write(ctx, BodyProblem(ctx), formErrors);
            var outcome = await RecordEngine.PrepareAsync(db, table, fields, obj);
            if (outcome.HasErrors) return ApiProblems.FromOutcome(ctx, outcome);
            if (table.IsProxy)
                return ApiError(ctx, ApiProblem.BadRequest, "Proxy tables forward to a remote API and cannot be written via the REST API.");
            if (!await RecordAccess.AllowsAsync(db, table, fields, Permission.Create, caller.Id, request: obj, callerRole: caller.Role))
                return ApiError(ctx, ApiProblem.Forbidden, "This record is not yours to create.");
            await MultipartRecord.SaveFilesAsync(ctx, obj);
            var record = new Record
            {
                TableId = table.Id,
                Id = Ids.NewShortId(12),
                JsonData = obj.ToJsonString(),
                CreatedAt = DateTime.UtcNow
            };
            db.Records.Add(record);
            await db.SaveChangesAsync();
            var created = await ApiLinks.ForRecordAsync(db, apiName, record, await ApiLinks.RelationsAsync(db, fields, ctx.RequestAborted), Array.Empty<ApiLinks.Relation>(), caller, ctx.RequestAborted);
            ApiConditional.SetETag(ctx, record);
            return Results.Created(ApiLinks.Self(apiName, record.Id), ApiDtos.RecordDto(record, fields, created.Links));
        });

        app.MapMethods("/api/v1/{apiName}/records/{rid}", new[] { "PATCH", "PUT" },
            async (AppDbContext db, HttpContext ctx, string apiName, string rid) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(ctx, ApiProblem.Unauthorized, "Missing or invalid bearer token.");
            var table = await db.Tables.Include(t => t.Fields).FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(ctx, ApiProblem.NotFound, "Table not found.");
            if (MethodGate(table, caller, ctx) is { } denied) return denied;
            if (AcceptGate(ctx) is { } unacceptable) return unacceptable;
            if (table.IsProxy) return ApiError(ctx, ApiProblem.BadRequest, "Proxy tables store nothing locally and cannot be updated.");

            if (MediaTypeGate(ctx) is { } unsupported) return unsupported;

            var record = await db.Records.FirstOrDefaultAsync(r => r.TableId == table.Id && r.Id == rid);
            if (record == null) return ApiError(ctx, ApiProblem.NotFound, "Record not found.");
            if (!ApiConditional.Matches(ctx, record))
                return ApiError(ctx, ApiProblem.PreconditionFailed, "The record changed since the version you hold. Re-read it and apply your change to the current one.");

            var fields = table.Fields.OrderBy(f => f.Position).ThenBy(f => f.Id).ToList();
            var (obj, formErrors) = await MultipartRecord.FromRequestAsync(ctx, fields);
            if (formErrors.Count > 0) return ApiProblems.Write(ctx, BodyProblem(ctx), formErrors);
            var replace = HttpMethods.IsPut(ctx.Request.Method);

            if (!await RecordAccess.AllowsAsync(db, table, fields, Permission.Update, caller.Id, rid, request: obj, callerRole: caller.Role))
                return ApiError(ctx, ApiProblem.Forbidden, "This record is not yours to change.");

            var (merged, outcome) = await RecordEngine.ApplyUpdateAsync(db, table, fields, record, obj, replace);
            if (outcome.HasErrors) return ApiProblems.FromOutcome(ctx, outcome);
            await MultipartRecord.SaveFilesAsync(ctx, merged);

            record.JsonData = merged.ToJsonString();
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException)
            {
                return ApiError(ctx, ApiProblem.Conflict, "Another write reached this record first. Re-read it and apply your change to the current one.");
            }
            var written = await ApiLinks.ForRecordAsync(db, apiName, record, await ApiLinks.RelationsAsync(db, fields, ctx.RequestAborted), Array.Empty<ApiLinks.Relation>(), caller, ctx.RequestAborted);
            ApiConditional.SetETag(ctx, record);
            return Results.Ok(ApiDtos.RecordDto(record, fields, written.Links));
        });

        app.MapDelete("/api/v1/{apiName}/records/{rid}", async (AppDbContext db, HttpContext ctx, string apiName, string rid) =>
        {
            if (await ApiAuth.ResolveAsync(db, ctx) is not { } caller) return ApiError(ctx, ApiProblem.Unauthorized, "Missing or invalid bearer token.");
            var table = await db.Tables.FirstOrDefaultAsync(t => t.ApiName == apiName && t.ApiEnabled);
            if (table == null) return ApiError(ctx, ApiProblem.NotFound, "Table not found.");
            if (MethodGate(table, caller, ctx) is { } denied) return denied;
            if (AcceptGate(ctx) is { } unacceptable) return unacceptable;
            var record = await db.Records.FirstOrDefaultAsync(r => r.TableId == table.Id && r.Id == rid);
            if (record == null) return ApiError(ctx, ApiProblem.NotFound, "Record not found.");
            if (!ApiConditional.Matches(ctx, record))
                return ApiError(ctx, ApiProblem.PreconditionFailed, "The record changed since the version you hold. Re-read it before deleting.");
            if (!await RecordAccess.AllowsAsync(db, table, await db.Fields.Where(f => f.TableId == table.Id).ToListAsync(), Permission.Delete, caller.Id, rid, callerRole: caller.Role))
                return ApiError(ctx, ApiProblem.Forbidden, "This record is not yours to delete.");
            db.Records.Remove(record);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException)
            {
                return ApiError(ctx, ApiProblem.Conflict, "Another write reached this record first. Re-read it before deleting.");
            }
            return Results.Ok(new { deleted = rid });
        });

    }

    private static async IAsyncEnumerable<object> Stream(
        IServiceScopeFactory scopes, string tableId, string? recordId, string userId, string callerRole,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var channel = RecordEvents.Subscribe();
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(token))
            {
                if (e.TableId != tableId) continue;
                if (recordId is not null && e.RecordId != recordId) continue;
                if (await AllowedFieldsAsync(scopes, tableId, userId, callerRole, e, token) is not { } fields) continue;
                yield return new
                {
                    action = e.Action,
                    id = e.RecordId,
                    record = EventRecord(e.Json, fields)
                };
            }
        }
        finally
        {
            RecordEvents.Unsubscribe(channel);
        }
    }

    private static async Task<List<FieldDefinition>?> AllowedFieldsAsync(
        IServiceScopeFactory scopes, string tableId, string userId, string callerRole, RecordEvent e, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var table = await db.Tables.FirstOrDefaultAsync(t => t.Id == tableId && t.ApiEnabled, token);
        if (table is null) return null;

        var fields = await db.Fields.Where(f => f.TableId == tableId).ToListAsync(token);
        if (!RecordAccess.HasRule(table, Permission.Read)) return fields;
        return await RecordAccess.AllowsAsync(db, table, fields, Permission.Read, userId,
            row: e.Json is null ? null : JsonNode.Parse(e.Json) as JsonObject, callerRole: callerRole) ? fields : null;
    }

    internal static JsonNode? EventRecord(string? json, IEnumerable<FieldDefinition> fields) =>
        json is null ? null : JsonNode.Parse(json) is JsonObject data ? ApiDtos.WithoutSecrets(data, fields) : null;

    private static IResult ApiError(HttpContext ctx, ApiProblem problem, string detail) =>
        ApiProblems.Write(ctx, problem, detail);

    private static ApiProblem BodyProblem(HttpContext ctx) =>
        MultipartRecord.Oversize(ctx) ? ApiProblem.TooLarge : ApiProblem.BadRequest;

    private static (List<QueryEngine.Filter> Filters, string? Error) ParseFilterParams(List<FieldDefinition> fields, string[]? raw)
    {
        var result = new List<QueryEngine.Filter>();
        foreach (var entry in raw ?? Array.Empty<string>())
        {
            var split = entry.IndexOf(':');
            if (split < 1) return (result, $"filter '{entry}' must be field:value.");
            var field = fields.FirstOrDefault(f => f.Name == entry[..split]);
            if (field is null) return (result, $"'{entry[..split]}' is not a filterable field.");
            result.Add(new QueryEngine.Filter(field, "eq", entry[(split + 1)..]));
        }
        return (result, null);
    }

    private static IResult? MethodGate(TableDefinition table, UserAccount caller, HttpContext ctx)
    {
        if (ApiMethods.Allows(table, caller, ctx.Request.Method)) return null;

        ctx.Response.Headers.Allow = string.Join(", ", ApiMethods.Parse(table.ApiMethods));
        var reason = ApiMethods.Allows(table, ctx.Request.Method)
            ? "This API key is not enabled for this method."
            : $"{ctx.Request.Method} is not enabled for this endpoint.";
        return ApiError(ctx, ApiProblem.MethodNotAllowed, reason);
    }

    private static IResult? AcceptGate(HttpContext ctx)
    {
        var accept = ctx.Request.Headers.Accept;
        if (accept.Count == 0) return null;

        foreach (var raw in accept)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var part in raw.Split(','))
            {
                var media = part.Split(';')[0].Trim();
                if (media.Length == 0) continue;
                if (media is "*/*" or "application/*" ||
                    media.Equals(ApiProblems.ContentType, StringComparison.OrdinalIgnoreCase) ||
                    media.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                    media.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
        }
        return ApiError(ctx, ApiProblem.NotAcceptable, "This API answers application/json only.");
    }

    private static IResult? MediaTypeGate(HttpContext ctx)
    {
        if (ctx.Request.HasFormContentType) return null;
        var type = ctx.Request.ContentType;
        if (string.IsNullOrWhiteSpace(type)) return null;
        if (type.Contains("json", StringComparison.OrdinalIgnoreCase)) return null;
        ctx.Response.Headers.Accept = "application/json, multipart/form-data";
        return ApiError(ctx, ApiProblem.UnsupportedMediaType, $"'{type}' is not a content type this endpoint accepts.");
    }
}
