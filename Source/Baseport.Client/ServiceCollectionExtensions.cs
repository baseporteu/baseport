using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Baseport.Client;

public sealed class BaseportOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
    public string? ApiToken { get; set; }
}

public static class ServiceCollectionExtensions
{
    public const string HttpClientName = "baseport";

    public static IServiceCollection AddBaseport(this IServiceCollection services, Action<BaseportOptions> configure)
    {
        var options = new BaseportOptions();
        configure(options);
        return Register(services, options);
    }

    public static IServiceCollection AddBaseport(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new BaseportOptions
        {
            BaseUrl = configuration["BaseUrl"] ?? new BaseportOptions().BaseUrl,
            ApiToken = configuration["ApiToken"]
        };
        return Register(services, options);
    }

    private static IServiceCollection Register(IServiceCollection services, BaseportOptions options)
    {
        services.AddHttpClient(HttpClientName, http => http.BaseAddress = new Uri(options.BaseUrl));

        services.AddSingleton<IBaseportClient>(provider =>
        {
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            var client = new BaseportClient(options.BaseUrl, http);
            if (!string.IsNullOrWhiteSpace(options.ApiToken)) client.UseApiToken(options.ApiToken!);
            return client;
        });

        return services;
    }
}
