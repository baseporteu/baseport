namespace Baseport;

public static class AuditLogMiddleware
{
    private const string NoteKey = "baseport.audit-note";

    public static void Note(HttpContext ctx, string message) =>
        ctx.Items[NoteKey] = ClientErrorEndpoints.Clean(message, 200);

    // routing ignores case
    internal static bool ShouldLog(string path, string method, bool hasNote)
    {
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/_admin/logs", StringComparison.OrdinalIgnoreCase)
            || path.Equals(ClientErrorEndpoints.Route, StringComparison.OrdinalIgnoreCase)) return false;

        return hasNote
            || !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            || path.StartsWith("/api/_admin", StringComparison.OrdinalIgnoreCase);
    }

    public static IApplicationBuilder UseAuditLog(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var method = context.Request.Method;

            await next();

            var note = context.Items[NoteKey] as string;
            if (!ShouldLog(path, method, note is not null)) return;

            context.RequestServices.GetRequiredService<AuditLogWriter>().Enqueue(new AuditLog
            {
                Id = Ids.NewShortId(12),
                CreatedAt = DateTime.UtcNow,
                Method = method,
                Path = path,
                Status = context.Response.StatusCode,
                Message = note ?? "",

                UserId = AdminAuth.UserIdFor(context) ?? "",
                ClientIp = RateLimit.ClientKey(context)
            });
        });
}
