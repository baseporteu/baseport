namespace Baseport;

public static class SameOriginWrites
{
    public static IApplicationBuilder UseSameOriginWrites(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (Refused(context.Request))
            {
                Serilog.Log.Warning("Refused a cross-origin {Method} to {Path} (Sec-Fetch-Site {Site}, Origin {Origin})",
                    context.Request.Method, context.Request.Path.Value,
                    context.Request.Headers["Sec-Fetch-Site"].ToString(), context.Request.Headers.Origin.ToString());
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { errors = new[] { "Cross-site request refused." } });
                return;
            }

            await next();
        });

    internal static bool Refused(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method)) return false;
        if (!request.Cookies.ContainsKey(AdminAuth.AuthCookie) && !request.Cookies.ContainsKey(AdminAuth.RefreshCookie)) return false;

        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0) return site is not ("same-origin" or "none");

        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0 && !string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}
