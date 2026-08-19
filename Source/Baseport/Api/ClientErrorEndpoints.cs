using System.Text;
using System.Text.Json.Nodes;

namespace Baseport;

// The console's scripts fail in a browser, where the operator is the only witness and a toast is gone in seconds. `ui.js` already surfaces them there; this is where the same line lands on the server.
public static class ClientErrorEndpoints
{
    public const string Route = "/api/client-errors";

    // What an audit row shows is one line in a table, so the message is capped at one.
    private const int MessageMax = 500;
    private const int PageMax = 200;

    // Not an HTTP exchange, so it carries no status. The logs view reads this to colour the row.
    public const string ClientMethod = "CLIENT";

    public static void MapClientErrorEndpoints(this WebApplication app)
    {
        app.MapPost(Route, (HttpContext ctx, AuditLogWriter audit, JsonObject body) =>
        {
            var message = Clean(Text(body, "message"), MessageMax);
            var page = Clean(Text(body, "page"), PageMax);

            // Nothing is said back either way: the browser sent this with sendBeacon and is not listening.
            if (message.Length == 0) return Results.NoContent();

            audit.Enqueue(new AuditLog
            {
                Id = Ids.NewShortId(12),
                CreatedAt = DateTime.UtcNow,
                Method = ClientMethod,
                Path = page,
                Status = 0,
                Message = message,
                UserId = AdminAuth.UserIdFor(ctx) ?? ""
            });

            // Also to the log file, because the failure that matters most is the one that stops the console loading, and an operator cannot read the logs view of a console that will not paint.
            Serilog.Log.Warning("Client error on {Page}: {Message}", page.Length > 0 ? page : "an unknown page", message);

            return Results.NoContent();
        }).RequireRateLimiting(RateLimit.ClientError);
    }

    // Anonymous input on its way to a log file: a newline would forge a second line, and a control character would corrupt the rest of it.
    internal static string Clean(string value, int max)
    {
        var sb = new StringBuilder(Math.Min(value.Length, max));
        var space = false;
        foreach (var c in value)
        {
            if (char.IsControl(c) || c == ' ')
            {
                // Collapsed rather than dropped, or two words run together.
                if (sb.Length > 0) space = true;
                continue;
            }
            // Counted before the write, not after: a pending space and its character are two, and checking afterwards lets the pair land one past the cap.
            var needed = space ? 2 : 1;
            if (sb.Length + needed > max) break;
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Text(JsonObject body, string name) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
