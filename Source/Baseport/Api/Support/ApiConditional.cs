using Microsoft.Net.Http.Headers;

namespace Baseport;

public static class ApiConditional
{
    public static EntityTagHeaderValue ETag(Record record) =>
        new($"\"{record.Id}-{record.UpdatedAt.Ticks}\"", isWeak: false);

    public static void SetETag(HttpContext ctx, Record record) =>
        ctx.Response.Headers.ETag = ETag(record).ToString();

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
