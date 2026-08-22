using Microsoft.Net.Http.Headers;

namespace Baseport;

// Conditional requests, RFC 9110 section 13. Without them PATCH and PUT are a lost update: two callers read one record, both write, and the second silently discards the first's change with nothing anywhere to say it happened.
public static class ApiConditional
{
    public static EntityTagHeaderValue ETag(Record record) =>
        new($"\"{record.Id}-{record.UpdatedAt.Ticks}\"", isWeak: false);

    public static void SetETag(HttpContext ctx, Record record) =>
        ctx.Response.Headers.ETag = ETag(record).ToString();

    // If-Match is honoured when sent and never required: demanding it would refuse every client written before it existed, which is the breaking half of the same feature.
    public static bool Matches(HttpContext ctx, Record record)
    {
        var header = ctx.Request.Headers.IfMatch;
        if (header.Count == 0) return true;

        var current = ETag(record);
        foreach (var raw in header)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.Trim() == "*") return true;
            if (!EntityTagHeaderValue.TryParseStrictList(raw.Split(','), out var tags)) continue;
            foreach (var tag in tags)
                if (tag.Compare(current, useStrongComparison: true)) return true;
        }
        return false;
    }

    // A read that already holds the current version gets 304 instead of the body again.
    public static bool NotModified(HttpContext ctx, Record record)
    {
        var header = ctx.Request.Headers.IfNoneMatch;
        if (header.Count == 0) return false;

        var current = ETag(record);
        foreach (var raw in header)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.Trim() == "*") return true;
            if (!EntityTagHeaderValue.TryParseStrictList(raw.Split(','), out var tags)) continue;
            foreach (var tag in tags)
                if (tag.Compare(current, useStrongComparison: false)) return true;
        }
        return false;
    }
}
