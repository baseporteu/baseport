using System.Net;
using System.Net.Sockets;
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
    public const string Docs = "docs";

    private static readonly (string Name, int PerMinute)[] Policies =
    {
        (Submit, 20), (Lookup, 10), (List, 60), (Schema, 60), (Auth, 10), (Oidc, 20), (ClientError, 10), (Upload, 30), (Docs, 60)
    };

    public static string ClientKey(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip ? Bucket(ip) : "unknown";

    // key ipv6 by /64 prefix
    internal static string Bucket(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

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
                    ApiProblems.Body(ctx.HttpContext, ApiProblem.TooManyRequests, "Too many requests. Wait a minute and try again."),
                    options: null, contentType: ApiProblems.ContentType, cancellationToken: token);
            };
        });
}
