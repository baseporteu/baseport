using Microsoft.AspNetCore.Http.Features;

namespace Baseport;

public static class BodyLimits
{
    public const long AuthBytes = 16 * 1024;
    public const long TransactionBytes = 4 * 1024 * 1024;

    public static long? For(PathString path) =>
        path.StartsWithSegments("/api/auth") ? AuthBytes
        : path.StartsWithSegments("/api/transaction") ? TransactionBytes
        : null;

    public static IApplicationBuilder UseBodyLimits(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            if (For(ctx.Request.Path) is not { } limit)
            {
                await next(ctx);
                return;
            }

            if (ctx.Request.ContentLength > limit)
            {
                await TooLargeAsync(ctx, limit);
                return;
            }

            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = limit;

            await next(ctx);

            if (ctx.Response.StatusCode == StatusCodes.Status413PayloadTooLarge && !ctx.Response.HasStarted)
                await TooLargeAsync(ctx, limit);
        });

    private static Task TooLargeAsync(HttpContext ctx, long limit)
    {
        ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return ctx.Response.WriteAsJsonAsync(
            ApiProblems.Body(ctx, ApiProblem.TooLarge, $"The request body exceeds {limit / 1024} KB."),
            options: null, contentType: ApiProblems.ContentType);
    }
}
