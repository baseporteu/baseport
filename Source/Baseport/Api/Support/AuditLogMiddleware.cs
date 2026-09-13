namespace Baseport;

public static class AuditLogMiddleware
{
    private const string NoteKey = "baseport.audit-note";

    public static void Note(HttpContext ctx, string message) =>
        ctx.Items[NoteKey] = ClientErrorEndpoints.Clean(message, 200);

    public static IApplicationBuilder UseAuditLog(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var method = context.Request.Method;

            await next();

            var note = context.Items[NoteKey] as string;

            if (method is "GET" or "HEAD" or "OPTIONS" && note is null) return;

            if (!path.StartsWith("/api", StringComparison.Ordinal) || path == "/api/_admin/logs" || path == ClientErrorEndpoints.Route) return;

            context.RequestServices.GetRequiredService<AuditLogWriter>().Enqueue(new AuditLog
            {
                Id = Ids.NewShortId(12),
                CreatedAt = DateTime.UtcNow,
                Method = method,
                Path = path,
                Status = context.Response.StatusCode,
                Message = note ?? "",

                UserId = AdminAuth.UserIdFor(context) ?? ""
            });
        });
}
