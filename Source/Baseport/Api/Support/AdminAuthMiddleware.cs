namespace Baseport;

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

        path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)

        || path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)

        || path.StartsWith("/api/forms/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/openapi.json", StringComparison.OrdinalIgnoreCase)

        || path.Equals(ClientErrorEndpoints.Route, StringComparison.OrdinalIgnoreCase);
}
