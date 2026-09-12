using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

// read paths for lookup and list forms
public static class QueryEngine
{
    public const int MaxPageSize = 200;

    // rows counted before the total stops being exact
    public const int CountCeiling = 2000;

    // an author-defined condition baked into a list form
    public sealed record Filter(FieldDefinition Field, string Operator, string Value);

    public static readonly string[] FilterOperators = { "eq", "ne", "gt", "lt", "contains" };

    // reads a form's stored filters, dropping any whose field no longer exists
    public static List<Filter> ParseFilters(IReadOnlyList<FieldDefinition> fields, JsonNode? node)
    {
        var result = new List<Filter>();
        if (node is not JsonArray arr) return result;
        foreach (var item in arr.OfType<JsonObject>())
        {
            var field = fields.FirstOrDefault(f => f.Name == (item["field"]?.GetValue<string>() ?? ""));
            if (field is null) continue;
            var op = item["op"]?.GetValue<string>() ?? "eq";
            if (!FilterOperators.Contains(op)) op = "eq";
            result.Add(new Filter(field, op, item["value"]?.ToString() ?? ""));
        }
        return result;
    }

    public sealed record ListPage(IReadOnlyList<Record> Records, int Total, int Page, int PageSize, bool HasMore)
    {
        public int TotalPages => PageSize <= 0 ? 1 : (int)Math.Ceiling(Total / (double)PageSize);

        // false once match count passed CountCeiling, Total is then a floor
        public bool CountExact => Total < CountCeiling;

        // set only on a keyset read, null means page-number paging or the last page of a walk
        public string? NextCursor { get; init; }
    }

    // ponytail: keyset only covers the (CreatedAt, Id) default order, widen for a sorted-column walk if needed
    public readonly record struct Cursor(DateTime CreatedAt, string Id)
    {
        public string Encode() => Base64Url(JsonSerializer.SerializeToUtf8Bytes(new CursorDto(CreatedAt.ToString("O", CultureInfo.InvariantCulture), Id)));

        public static Cursor? Decode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try
            {
                var padded = value.Replace('-', '+').Replace('_', '/');
                padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
                var dto = JsonSerializer.Deserialize<CursorDto>(Convert.FromBase64String(padded));
                if (dto is null || string.IsNullOrEmpty(dto.I)) return null;
                if (!DateTime.TryParse(dto.C, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created)) return null;
                return new Cursor(created, dto.I);
            }
            catch (Exception e) when (e is JsonException or FormatException or ArgumentException)
            {
                return null;
            }
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed record CursorDto(string C, string I);

    // exact, case-insensitive match of term against any identifier field
    public static async Task<Record?> LookupAsync(AppDbContext db, TableDefinition table, IReadOnlyList<FieldDefinition> matchFields, string term)
    {
        if (matchFields.Count == 0 || string.IsNullOrWhiteSpace(term)) return null;

        // LIKE without wildcards is exact but case-insensitive in SQLite, matches a human-typed identifier
        var conditions = string.Join(" OR ", matchFields.Select(f => $"{Column(f)} LIKE {{1}} ESCAPE '\\'"));
        var sql = $$"""
            SELECT r."Id", r."TableId", r."JsonData", r."CreatedAt", r."UpdatedAt"
            FROM "_records" r
            WHERE r."TableId" = {0} AND ({{conditions}})
            ORDER BY r."CreatedAt" DESC
            LIMIT 1
            """;
        var rows = await db.Records.FromSqlRaw(sql, table.Id, EscapeLike(term.Trim())).AsNoTracking().ToListAsync();
        return rows.FirstOrDefault();
    }

    // paged, optionally searched and sorted overview for a list form or the records grid
    public static async Task<ListPage> ListAsync(
        AppDbContext db,
        TableDefinition table,
        IReadOnlyList<FieldDefinition> searchFields,
        FieldDefinition? sortField,
        bool sortDescending,
        string? query,
        int page,
        int pageSize,
        IReadOnlyList<Filter>? filters = null,
        IReadOnlyList<FieldDefinition>? accessFields = null,
        string? accessUserId = null,
        Cursor? cursor = null,
        string? systemSort = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, MaxPageSize);

        var args = new List<object> { table.Id };
        var where = "r.\"TableId\" = {0}";

        // access rule applies first, a filter or search term can only narrow it further
        if (accessFields is not null && RecordAccess.ListClause(table, accessFields, "r", accessUserId, args) is { } clause)
            where += $" AND ({clause})";

        // form-defined filters apply before the search box narrows further
        foreach (var f in filters ?? Array.Empty<Filter>())
        {
            var slot = args.Count;
            args.Add(f.Value);
            where += f.Operator switch
            {
                "ne" => $" AND {Column(f.Field)} <> {{{slot}}}",
                "gt" => $" AND CAST({Column(f.Field)} AS REAL) > CAST({{{slot}}} AS REAL)",
                "lt" => $" AND CAST({Column(f.Field)} AS REAL) < CAST({{{slot}}} AS REAL)",
                "contains" => $" AND {Column(f.Field)} LIKE '%' || {{{slot}}} || '%'",
                _ => $" AND {Column(f.Field)} = {{{slot}}}"
            };
        }

        var search = "";
        string? rankJoin = null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var slot = args.Count;
            var term = query.Trim();
            // /pattern/ runs it through SQLite's regexp() (registered in SqlitePragmas, time-boxed against ReDoS); a bare * or % switches to a raw LIKE pattern instead of an escaped literal
            var isRegex = term.Length > 2 && term[0] == '/' && term[^1] == '/';
            var hasWildcard = !isRegex && (term.Contains('*') || term.Contains('%'));
            var op = isRegex ? "REGEXP" : "LIKE";
            var escapeClause = isRegex || hasWildcard ? "" : " ESCAPE '\\'";

            string? match = null;
            if (!isRegex && !hasWildcard && searchFields.Count == 0 && RecordSearch.MatchExpression(table.Id, term) is { } expression && await RecordSearch.AvailableAsync(db))
                match = expression;

            var pattern = isRegex ? term[1..^1]
                : hasWildcard ? term.Replace('*', '%')
                : $"%{EscapeLike(term)}%";
            args.Add(match ?? pattern);
            if (searchFields.Count > 0)
                // restricted search: only the columns the form exposes
                search = " AND (" + string.Join(" OR ", searchFields.Select(f => $"{Column(f)} {op} {{{slot}}}{escapeClause}")) + ")";
            else if (match is not null)
            {
                // fts5 match: whole words and prefixes only, trade for not scanning every record
                search = RecordSearch.Clause("r", slot);
                rankJoin = RecordSearch.RankJoin(slot);
            }
            else
                // no fts5 match (or a regex/wildcard query, which fts5 can't run): json_each scan instead
                search = $" AND EXISTS (SELECT 1 FROM json_each(r.\"JsonData\") je WHERE je.value {op} {{{slot}}}{escapeClause})";
        }

        // counting doubles the work, most callers only render it as "page 1 of n"
        var countSql = $"""
            SELECT COUNT(*) AS "Value" FROM (
                SELECT 1 FROM "_records" r WHERE {where}{search} LIMIT {CountCeiling}
            )
            """;
        var total = await db.Database.SqlQueryRaw<int>(countSql, args.ToArray()).SingleAsync();

        // a named sort field always wins, relevance ranking only fills in the default order
        var ranked = rankJoin is not null && sortField is null;
        var order = ranked
            ? "m.\"Rank\""
            : sortField is not null
                ? Column(sortField)
                : systemSort == "UpdatedAt" ? "r.\"UpdatedAt\"" : "r.\"CreatedAt\"";
        var direction = ranked || !sortDescending ? "ASC" : "DESC";

        // walks from the last row instead of counting rows to skip, only valid on the (CreatedAt, Id) default order
        var keyset = "";
        if (cursor is { } from)
        {
            var createdSlot = args.Count;
            args.Add(from.CreatedAt);
            var idSlot = args.Count;
            args.Add(from.Id);
            keyset = $" AND (r.\"CreatedAt\" < {{{createdSlot}}} OR (r.\"CreatedAt\" = {{{createdSlot}}} AND r.\"Id\" < {{{idSlot}}}))";
        }

        var pageSql = $$"""
            SELECT r."Id", r."TableId", r."JsonData", r."CreatedAt", r."UpdatedAt"
            FROM "_records" r{{(ranked ? rankJoin : "")}}
            WHERE {{where}}{{(ranked ? "" : search)}}{{keyset}}
            ORDER BY {{order}} {{direction}}, r."Id" DESC
            LIMIT {{pageSize + 1}} OFFSET {{(keyset.Length > 0 ? 0 : (page - 1) * pageSize)}}
            """;
        var records = await db.Records.FromSqlRaw(pageSql, args.ToArray()).AsNoTracking().ToListAsync();

        // one row past the page size signals there is a next page
        var hasMore = records.Count > pageSize;
        if (hasMore) records.RemoveAt(records.Count - 1);

        var next = hasMore && records.Count > 0 && CursorsApply(sortField, rankJoin, systemSort)
            ? new Cursor(records[^1].CreatedAt, records[^1].Id).Encode()
            : null;
        return new ListPage(records, total, page, pageSize, hasMore) { NextCursor = next };
    }

    // cursors only match the (CreatedAt, Id) order, a sort field, a system sort other than the default, or relevance ranking all break that key
    public static bool CursorsApply(FieldDefinition? sortField, string? rankJoin, string? systemSort = null) =>
        sortField is null && rankJoin is null && systemSort is null or "CreatedAt";

    // projects a record down to the fields a public form may reveal
    public static JsonObject Project(Record record, IReadOnlyList<FieldDefinition> visible)
    {
        var source = JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject ?? new JsonObject();
        var result = new JsonObject();
        foreach (var f in visible)
            result[f.Name] = source.TryGetPropertyValue(f.Name, out var v) ? v?.DeepClone() : null;
        return result;
    }

    // prefers RecordIndexes' generated column, falls back to json_extract for types with none
    private static string Column(FieldDefinition field) =>
        RecordIndexes.ColumnFor(field) is { } column
            ? $"r.\"{column}\""
            : JsonPath(field.Name);

    // access rules read JSON directly, the generated column may not exist yet on an unsynced table
    internal static string JsonPathFor(string fieldName, string alias) => JsonPath(fieldName, alias);

    private static string JsonPath(string fieldName, string alias = "r") =>
        $"json_extract({alias}.\"JsonData\", '$.\"{fieldName.Replace("'", "''").Replace("\"", "\"\"")}\"')";

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // resolves stored field names to live fields, dropping anything stale
    public static List<FieldDefinition> Resolve(IReadOnlyList<FieldDefinition> fields, JsonNode? names)
    {
        if (names is not JsonArray arr) return new List<FieldDefinition>();
        var wanted = arr.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
        return wanted
            .Select(n => fields.FirstOrDefault(f => f.Name == n))
            .Where(f => f is not null)
            .Select(f => f!)
            .ToList();
    }

    public static JsonObject ParseConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return new JsonObject();
        try { return JsonNode.Parse(configJson) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
}
