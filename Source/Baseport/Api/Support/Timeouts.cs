using Microsoft.AspNetCore.Http.Timeouts;

namespace Baseport;

public static class Timeouts
{
    public const string Long = "long";

    public static readonly TimeSpan Default = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(10);

    public static void AddBaseportTimeouts(this IServiceCollection services) =>
        services.AddRequestTimeouts(o =>
        {
            o.DefaultPolicy = new RequestTimeoutPolicy { Timeout = Default, TimeoutStatusCode = StatusCodes.Status504GatewayTimeout };
            o.AddPolicy(Long, new RequestTimeoutPolicy { Timeout = LongTimeout, TimeoutStatusCode = StatusCodes.Status504GatewayTimeout });
        });
}
