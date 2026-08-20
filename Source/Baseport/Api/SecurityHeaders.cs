namespace Baseport;

// Global security headers middleware
public static class SecurityHeaders
{
    // Strict CSP for console routes (disallows framing)
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

    private const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), " +
        "fullscreen=(self), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), " +
        "midi=(), payment=(), picture-in-picture=(), publickey-credentials-get=(), " +
        "screen-wake-lock=(), usb=(), xr-spatial-tracking=()";

    // Rendered inside somebody else's page on purpose, so these carry the dynamic frame-ancestors policy instead of the console's.
    private static bool IsEmbeddable(string path) =>
        path.StartsWith("/preview/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/forms/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/embed.js", StringComparison.OrdinalIgnoreCase);

    // Fetchable from another origin, a wider set than the framable one: a file field stores an absolute URL meant to load like any other asset, so an upload must not carry a same-origin resource policy. It keeps the console's CSP and X-Frame-Options, because being fetchable is not being framable.
    private static bool IsCrossOriginFetchable(string path) =>
        IsEmbeddable(path) || path.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase);

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // TRACE is never routed here
            if (HttpMethods.IsTrace(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS";
                return;
            }

            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            headers["Permissions-Policy"] = PermissionsPolicy;
            headers["X-Permitted-Cross-Domain-Policies"] = "none";

            var path = context.Request.Path.Value ?? "";
            var embeddable = IsEmbeddable(path);

            headers["Cross-Origin-Resource-Policy"] = IsCrossOriginFetchable(path) ? "cross-origin" : "same-origin";
            if (!embeddable) headers["Cross-Origin-Opener-Policy"] = "same-origin";

            if (!embeddable)
            {
                headers["Content-Security-Policy"] = ConsolePolicy;
                headers["X-Frame-Options"] = "DENY";
            }
            else
            {
                // Dynamic frame-ancestors CSP for embeddable endpoints
                headers["Content-Security-Policy"] =
                    $"frame-ancestors {AllowedOrigins.FrameAncestors(EmbedOrigins.Current)}";
            }

            await next();
        });
}
