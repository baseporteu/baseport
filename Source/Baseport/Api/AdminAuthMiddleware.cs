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

            var db = context.RequestServices.GetRequiredService<AppDbContext>();
            var user = await AdminAuth.ResolveAsync(db, context);

            if (user is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { errors = new[] { "Sign in to continue." } });
                return;
            }

            // A session is not enough: /api/auth/otp will hand one to any account that is not disabled, including one that exists only to carry an API token, and the public surface mints the same token for an end user.
            if (user.Role != AccountRoles.Admin)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { errors = new[] { "This account does not have console access." } });
                return;
            }

            if (user.MustChangePassword)
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
        || path.Equals("/api/openapi.json", StringComparison.OrdinalIgnoreCase)
        // Where the browser reports that a script died. Deliberately open, because the failure worth hearing about most is the one on the sign-in screen, before there is a session to authenticate it with.
        || path.Equals(ClientErrorEndpoints.Route, StringComparison.OrdinalIgnoreCase);
}
