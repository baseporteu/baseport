using System.Threading.RateLimiting;

namespace Baseport;

public static class RateLimit
{
    public const string Submit = "form-submit";
    public const string Lookup = "form-lookup";
    public const string List = "form-list";
    public const string Schema = "form-schema";
    public const string Auth = "auth";
    public const string Oidc = "auth-oidc";
    public const string ClientError = "client-error";
    public const string Upload = "upload";

    private static readonly (string Name, int PerMinute)[] Policies =
    {
        (Submit, 20), (Lookup, 10), (List, 60), (Schema, 60), (Auth, 10), (Oidc, 20), (ClientError, 10), (Upload, 30)
    };

    public static string ClientKey(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static string PartitionKey(HttpContext ctx, string policy) =>
        $"{policy}:{ctx.Request.RouteValues["fpid"] ?? ""}:{ClientKey(ctx)}";

    public static void AddBaseportRateLimiter(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            foreach (var (name, perMinute) in Policies)
            {
                var policy = name;
                var budget = perMinute;
                options.AddPolicy(policy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(ctx, policy),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = budget, Window = TimeSpan.FromMinutes(1) }));
            }

            options.OnRejected = async (ctx, token) =>
            {
                ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                ctx.HttpContext.Response.Headers.RetryAfter = "60";
                ctx.HttpContext.Response.ContentType = ApiProblems.ContentType;
                await ctx.HttpContext.Response.WriteAsJsonAsync(
                    ApiProblems.Body(ctx.HttpContext, ApiProblem.TooManyRequests, "Too many requests. Wait a minute and try again."), token);
            };
        });
}
