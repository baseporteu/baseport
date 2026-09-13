namespace Baseport;

public static class EmbedOrigins
{
    private static volatile string[] _current = Array.Empty<string>();

    public static IReadOnlyList<string> Current => _current;

    public static void Set(string? stored) => _current = AllowedOrigins.Parse(stored).ToArray();
}

public static class AllowedOrigins
{

    public static List<string> Parse(string? stored) =>
        (stored ?? "")
            .Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string Serialize(IEnumerable<string> origins) => string.Join("\n", origins);

    public static bool Allows(IReadOnlyList<string> allowed, string? origin) =>
        allowed.Count == 0
        || (origin is not null && allowed.Contains(Normalize(origin), StringComparer.OrdinalIgnoreCase));

    public static string Normalize(string raw)
    {
        var value = (raw ?? "").Trim().TrimEnd('/');
        if (value.Length == 0) return "";
        if (value == "*") return "*";

        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https"
            ? uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}"
            : "";
    }

    public static string FrameAncestors(IReadOnlyList<string> allowed) =>
        allowed.Count == 0 || allowed.Contains("*") ? "*" : string.Join(" ", allowed);
}