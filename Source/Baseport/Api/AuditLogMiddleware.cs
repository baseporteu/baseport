namespace Baseport;

// Records every mutating /api request.
public static class AuditLogMiddleware
{
    private const string NoteKey = "baseport.audit-note";

    // What this request meant, in the words an operator reads in the logs view. Anything a caller supplied is cleaned and capped on the way in: it lands in a column that is rendered as markup.
    public static void Note(HttpContext ctx, string message) =>
        ctx.Items[NoteKey] = ClientErrorEndpoints.Clean(message, 200);

    public static IApplicationBuilder UseAuditLog(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var method = context.Request.Method;

            await next();

            // Set by the endpoint while it ran, so one row includes both what was requested and what it meant. A sign-in that failed has no session to name the caller by, and an OpenID Connect return is a GET that would otherwise never be recorded at all.
            var note = context.Items[NoteKey] as string;

            if (method is "GET" or "HEAD" or "OPTIONS" && note is null) return;
            // The client-error route writes its own row, with the message in it; this one would only add a contentless duplicate.
            if (!path.StartsWith("/api", StringComparison.Ordinal) || path == "/api/_admin/logs" || path == ClientErrorEndpoints.Route) return;

            context.RequestServices.GetRequiredService<AuditLogWriter>().Enqueue(new AuditLog
            {
                Id = Ids.NewShortId(12),
                CreatedAt = DateTime.UtcNow,
                Method = method,
                Path = path,
                Status = context.Response.StatusCode,
                Message = note ?? "",
                // Read after the request: a sign-in arrives without a session and answers with one.
                UserId = AdminAuth.UserIdFor(context) ?? ""
            });
        });
}
