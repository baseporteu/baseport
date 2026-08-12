namespace Baseport;

// Global security headers middleware.
public static class SecurityHeaders
{
    // Strict CSP for console routes (disallows framing).
    private const string ConsolePolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "same-origin";

            var path = context.Request.Path.Value ?? "";
            if (!path.StartsWith("/preview/", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith("/api/forms/", StringComparison.OrdinalIgnoreCase)
                && !path.Equals("/embed.js", StringComparison.OrdinalIgnoreCase))
            {
                headers["Content-Security-Policy"] = ConsolePolicy;
                headers["X-Frame-Options"] = "DENY";
            }
            else
            {
                // Dynamic frame-ancestors CSP for embeddable endpoints.
                headers["Content-Security-Policy"] =
                    $"frame-ancestors {AllowedOrigins.FrameAncestors(EmbedOrigins.Current)}";
            }

            await next();
        });
}