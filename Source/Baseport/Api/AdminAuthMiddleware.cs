namespace Baseport;

// Closed by default: everything under /api needs a console session unless its prefix is listed here, so a new endpoint is protected the moment it is added.
public static class AdminAuthMiddleware
{
    public static IApplicationBuilder UseAdminAuth(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";

            if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) || IsPublicPath(path))
            {
                await next();
                return;
            }

            if (AdminAuth.UserIdFor(context) is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { errors = new[] { "Sign in to continue." } });
                return;
            }

            // A session is not enough: /api/auth/otp will hand one to any account that is not disabled, including one that exists only to carry an API token.
            if (!AdminAuth.IsAdmin(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { errors = new[] { "This account does not have console access." } });
                return;
            }

            if (AdminAuth.MustChangePassword(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { errors = new[] { "Change the one-time password before using the console." } });
                return;
            }

            await next();
        });

    internal static bool IsPublicPath(string path) =>
        // Sign-in surface, reachable precisely because there is no session yet.
        path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
        // Carries its own bearer token and its own per-table switches.
        || path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
        // Anonymous form traffic. Management lives under /api/_admin/forms.
        || path.StartsWith("/api/forms/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/openapi.json", StringComparison.OrdinalIgnoreCase);
}
