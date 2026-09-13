namespace Baseport;

public static class AdminSurface
{
    public static int? Port { get; private set; }

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

    internal static bool IsAdminPath(string path) =>
        !path.StartsWith("/api/auth/v1", StringComparison.OrdinalIgnoreCase)
        && (path.StartsWith("/_/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/_", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/_admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/fragments", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase));
}
