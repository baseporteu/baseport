using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Baseport;

public static class OpenApiCache
{
    public sealed record Document(long Version, string Json, string ETag);

    private static long _version;
    private static Document? _baked;

    public static void Invalidate() => Interlocked.Increment(ref _version);

    public static long CurrentVersion => Volatile.Read(ref _version);

    public static Document? Get(long version) =>
        Volatile.Read(ref _baked) is { } baked && baked.Version == version ? baked : null;

    public static Document Set(long version, string json)
    {
        var next = new Document(version, json, TagFor(json));
        while (true)
        {
            var current = Volatile.Read(ref _baked);
            if (current is not null && current.Version > version) return next;
            if (Interlocked.CompareExchange(ref _baked, next, current) == current) return next;
        }
    }

    public static bool NotModified(StringValues ifNoneMatch, Document document)
    {
        if (ifNoneMatch.Count == 0) return false;
        var current = new EntityTagHeaderValue(document.ETag);
        foreach (var raw in ifNoneMatch)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.Trim() == "*") return true;
            if (!EntityTagHeaderValue.TryParseStrictList(raw.Split(','), out var tags)) continue;
            if (tags.Any(tag => tag.Compare(current, useStrongComparison: false))) return true;
        }
        return false;
    }

    private static string TagFor(string json) =>
        $"\"{Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(json)).AsSpan(0, 16))}\"";
}
