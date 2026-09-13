using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using DiceBear;
using Serilog;

namespace Baseport;

public static class Avatars
{

    private static readonly Lazy<Style?> Slice = new(() =>
    {
        try
        {
            return Style.Parse(Styles.Slice);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the DiceBear slice style; the console falls back to an initial");
            return null;
        }
    });

    private static readonly JsonArray Backgrounds =
        new("ffe3ea", "e3edff", "e2f5e9", "fdf1d4", "efe6ff");

    private const int MaxCached = 256;
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string? DataUri(string? seed)
    {
        if (string.IsNullOrWhiteSpace(seed)) return null;
        if (Cache.TryGetValue(seed, out var cached)) return cached;

        if (Slice.Value is not { } style) return null;

        try
        {
            var avatar = new Avatar(style, new JsonObject
            {
                ["backgroundColor"] = Backgrounds.DeepClone(),
                ["seed"] = seed,
            });
            var uri = avatar.ToDataUri();

            if (Cache.Count < MaxCached) Cache[seed] = uri;
            return uri;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not render an avatar for {Seed}", seed);
            return null;
        }
    }
}
