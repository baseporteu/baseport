using System.Diagnostics;
using Serilog;

namespace Baseport;

// One line per proxied call.
public static class ProxyLog
{
    // Times a proxied call and logs its outcome. Never logs the token.
    public static async Task<T> TraceAsync<T>(string operation, string table, string method, string url, Func<Task<T>> call, Func<T, string> describe)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await call();
            Log.Information("Proxy {Operation} {Table} {Method} {Url} -> {Outcome} in {Elapsed:0}ms",
                operation, table, method, Redact(url), describe(result), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            Log.Warning("Proxy {Operation} {Table} {Method} {Url} -> {Error} in {Elapsed:0}ms",
                operation, table, method, Redact(url), ex.Message, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    // A target URL can carry credentials in its query string, and these lines land in a file an operator may share.
    public static string Redact(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query)) return url;

        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var safe = parts.Select(p =>
        {
            var name = p.Split('=')[0];
            return Secretish.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)) ? $"{name}=***" : p;
        });
        return $"{uri.GetLeftPart(UriPartial.Path)}?{string.Join("&", safe)}";
    }

    private static readonly string[] Secretish = { "token", "key", "secret", "password", "auth", "sig" };
}
