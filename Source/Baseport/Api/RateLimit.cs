using System.Threading.RateLimiting;

namespace Baseport;

// Budgets for the anonymous surface. A lookup without one is a brute-force oracle for identifiers.
public static class RateLimit
{
    public const string Submit = "form-submit";
    public const string Lookup = "form-lookup";
    public const string List = "form-list";
    public const string Schema = "form-schema";
    public const string Auth = "auth";
    public const string Oidc = "auth-oidc";
    public const string ClientError = "client-error";

    private static readonly (string Name, int PerMinute)[] Policies =
    {
        (Submit, 20), (Lookup, 10), (List, 60), (Schema, 60), (Auth, 10), (Oidc, 20), (ClientError, 10)
    };

    // the socket address only, nothing a caller can set; UseForwardedHeaders rewrites it behind a trusted proxy
    public static string ClientKey(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // one bucket per client per form, one noisy visitor or abused form can't exhaust the others
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
                await ctx.HttpContext.Response.WriteAsJsonAsync(
                    new { errors = new[] { "Too many requests. Wait a minute and try again." } }, token);
            };
        });
}
