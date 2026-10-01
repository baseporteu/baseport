using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class ConnectionEndpoints
{
    public static void MapConnectionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/_admin/connections", async (AppDbContext db) =>
            Results.Ok((await db.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync()).Select(Connections.Dto)));

        app.MapPost("/api/_admin/connections", async (AppDbContext db, JsonObject body) =>
        {
            var now = DateTime.UtcNow;
            var connection = new Connection { Id = Ids.NewShortId(12), CreatedAt = now, UpdatedAt = now };
            Apply(connection, body);
            if (await Connections.ProblemsAsync(db, connection) is { Count: > 0 } errors) return Results.BadRequest(new { errors });

            db.Connections.Add(connection);
            await db.SaveChangesAsync();
            return Results.Ok(Connections.Dto(connection));
        });

        app.MapPatch("/api/_admin/connections/{id}", async (AppDbContext db, string id, JsonObject body) =>
        {
            var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == id);
            if (connection is null) return Results.NotFound();

            Apply(connection, body);
            if (await Connections.ProblemsAsync(db, connection) is { Count: > 0 } errors) return Results.BadRequest(new { errors });

            connection.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(Connections.Dto(connection));
        });

        app.MapPost("/api/_admin/connections/{id}/test", async (AppDbContext db, IHttpClientFactory clients, HttpContext ctx, string id, JsonObject body) =>
        {
            var connection = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (connection is null) return Results.NotFound();

            if (ConnectionProtocols.IsSql(connection.Protocol))
            {
                try
                {
                    var tables = await SqlSource.TablesAsync(db, connection, ctx.RequestAborted);
                    return Results.Ok(new { tables = tables.Count });
                }
                catch (RemoteFetch.FetchException ex)
                {
                    return Results.BadRequest(new { errors = new[] { ex.Message } });
                }
            }

            var options = new RemoteFetch.Options(Text(body, "path") ?? "", Text(body, "paging") ?? "auto", Text(body, "recordsPointer"), MaxPages: 1);
            if (!RemoteFetch.Strategies.Contains(options.Paging)) return Results.BadRequest(new { errors = new[] { "Unknown paging strategy." } });

            var report = new RemoteFetch.Report();
            var rows = new List<JsonObject>();
            try
            {
                await foreach (var page in RemoteFetch.PagesAsync(db, clients.CreateClient(), connection, options, report, ctx.RequestAborted))
                    rows.AddRange(page.Records);
            }
            catch (RemoteFetch.FetchException ex)
            {
                return Results.BadRequest(new { errors = new[] { ex.Message } });
            }

            return Results.Ok(new
            {
                rows = rows.Count,
                strategy = report.Strategy,
                fields = rows.SelectMany(r => r.Select(p => p.Key)).Distinct().Take(200),
                sample = rows.Take(3)
            });
        }).WithRequestTimeout(Timeouts.Long);

        app.MapGet("/api/_admin/connections/{id}/tables", async (AppDbContext db, IHttpClientFactory clients, HttpContext ctx, string id) =>
        {
            var connection = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (connection is null) return Results.NotFound();
            if (connection.Protocol != ConnectionProtocols.Baseport && !ConnectionProtocols.IsSql(connection.Protocol))
                return Results.BadRequest(new { errors = new[] { "Only a Baseport or SQL connection can list its tables." } });

            try
            {
                if (ConnectionProtocols.IsSql(connection.Protocol))
                    return Results.Ok((await SqlSource.TablesAsync(db, connection, ctx.RequestAborted))
                        .Select(t => new BaseportSource.RemoteTable(t.Display, t.Display)));
                return Results.Ok(await BaseportSource.TablesAsync(db, clients.CreateClient(), connection, ctx.RequestAborted));
            }
            catch (RemoteFetch.FetchException ex)
            {
                return Results.BadRequest(new { errors = new[] { ex.Message } });
            }
        }).WithRequestTimeout(Timeouts.Long);

        app.MapDelete("/api/_admin/connections/{id}", async (AppDbContext db, string id) =>
        {
            var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == id);
            if (connection is null) return Results.NotFound();
            if (await db.Clones.Where(c => c.ConnectionId == id).Select(c => c.Name).ToListAsync() is { Count: > 0 } clones)
                return Results.Conflict(new { errors = new[] { $"'{connection.Name}' is used by {string.Join(", ", clones.Select(n => $"clone {n}"))}." } });

            db.Connections.Remove(connection);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = connection.Id });
        });
    }

    private static void Apply(Connection c, JsonObject body)
    {
        if (Text(body, "name") is { } name) c.Name = name;
        if (Text(body, "baseUrl") is { } baseUrl) c.BaseUrl = baseUrl;
        if (Text(body, "protocol") is { } protocol) c.Protocol = protocol;
        if (Text(body, "authKind") is { } authKind) c.AuthKind = authKind;
        if (Text(body, "authHeaderName") is { } headerName) c.AuthHeaderName = headerName;
        if (Text(body, "basicUsername") is { } username) c.BasicUsername = username;
        if (Text(body, "authSecretId") is { } secretId) c.AuthSecretId = secretId;
        if (body["headers"] is JsonArray headers) c.HeadersJson = headers.ToJsonString();
    }

    private static string? Text(JsonObject body, string name) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
