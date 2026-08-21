namespace Baseport;

// Moves the operator surface onto a second port so it can be bound to a private interface,. Unset means one port and no filtering, which is the default.
public static class AdminSurface
{
    public static int? Port { get; private set; }

    // Only the public port is filtered. Blocking public routes on the private port would buy nothing: the point is to shrink what the internet can reach, not to partition the operator's own view.
    public static string? Configure(string? address)
    {
        Port = null;
        if (string.IsNullOrWhiteSpace(address)) return null;

        var url = address.Contains("://", StringComparison.Ordinal) ? address.Trim() : $"http://{address.Trim()}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Port <= 0)
            throw new InvalidOperationException($"Baseport:AdminAddress is not a valid address: {address}");

        Port = parsed.Port;
        return url;
    }

    public static IApplicationBuilder UseAdminSurface(this IApplicationBuilder app) =>
        Port is null ? app : app.Use(async (context, next) =>
        {
            if (context.Connection.LocalPort != Port && IsAdminPath(context.Request.Path.Value ?? ""))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                    await context.Response.WriteAsJsonAsync(new { errors = new[] { "No such endpoint." } });
                return;
            }

            await next();
        });

    // /api/auth/v1 is the public end-user surface and is checked first, because /api/auth is the console's own sign-in.
    internal static bool IsAdminPath(string path) =>
        !path.StartsWith("/api/auth/v1", StringComparison.OrdinalIgnoreCase)
        && (path.StartsWith("/_/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/_", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/_admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/fragments", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase));
}
