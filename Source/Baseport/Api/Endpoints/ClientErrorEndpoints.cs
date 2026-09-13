using System.Text;
using System.Text.Json.Nodes;

namespace Baseport;

public static class ClientErrorEndpoints
{
    public const string Route = "/api/client-errors";

    private const int MessageMax = 500;
    private const int PageMax = 200;

    public const string ClientMethod = "CLIENT";

    public static void MapClientErrorEndpoints(this WebApplication app)
    {
        app.MapPost(Route, (HttpContext ctx, AuditLogWriter audit, JsonObject body) =>
        {
            var message = Clean(Text(body, "message"), MessageMax);
            var page = Clean(Text(body, "page"), PageMax);

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

            Serilog.Log.Warning("Client error on {Page}: {Message}", page.Length > 0 ? page : "an unknown page", message);

            return Results.NoContent();
        }).RequireRateLimiting(RateLimit.ClientError);
    }

    internal static string Clean(string value, int max)
    {
        var sb = new StringBuilder(Math.Min(value.Length, max));
        var space = false;
        foreach (var c in value)
        {
            if (char.IsControl(c) || c == ' ')
            {

                if (sb.Length > 0) space = true;
                continue;
            }

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
