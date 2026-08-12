namespace Baseport;

// Records every mutating /api request.
public static class AuditLogMiddleware
{
    public static IApplicationBuilder UseAuditLog(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var method = context.Request.Method;

            await next();

            if (method is "GET" or "HEAD" or "OPTIONS") return;
            if (!path.StartsWith("/api", StringComparison.Ordinal) || path == "/api/_admin/logs") return;

            context.RequestServices.GetRequiredService<AuditLogWriter>().Enqueue(new AuditLog
            {
                Id = Ids.NewShortId(12),
                CreatedAt = DateTime.UtcNow,
                Method = method,
                Path = path,
                Status = context.Response.StatusCode,
                // Read after the request: a sign-in arrives without a session and answers with one.
                UserId = AdminAuth.UserIdFor(context) ?? ""
            });
        });
}
