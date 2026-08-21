using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baseport;

// Read paths for LOOKUP and LIST forms.
public static class QueryEngine
{
    public const int MaxPageSize = 200;

    // Rows counted before the total stops being exact.
    public const int CountCeiling = 2000;

    // An author-defined condition baked into a list form.
    public sealed record Filter(FieldDefinition Field, string Operator, string Value);

    public static readonly string[] FilterOperators = { "eq", "ne", "gt", "lt", "contains" };

    // Reads a form's stored filters, dropping any whose field no longer exists.
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

        // False once the match count passed CountCeiling; Total is then a floor.
        public bool CountExact => Total < CountCeiling;
    }

    // Exact, case-insensitive match of term against any of the identifier fields.
    public static async Task<Record?> LookupAsync(AppDbContext db, TableDefinition table, IReadOnlyList<FieldDefinition> matchFields, string term)
    {
        if (matchFields.Count == 0 || string.IsNullOrWhiteSpace(term)) return null;

        // One json_extract per identifier field; LIKE without wildcards is an exact but case-insensitive comparison in SQLite, which is what a human-typed identifier needs.
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

    // Paged, optionally searched and sorted overview for a LIST form or the records grid.
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
        string? accessUserId = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, MaxPageSize);

        var args = new List<object> { table.Id };
        var where = "r.\"TableId\" = {0}";

        // Applied before anything the caller controls, a filter or a search term can only narrow what the rule already allows.
        if (accessFields is not null && RecordAccess.ListClause(table, accessFields, "r", accessUserId, args) is { } clause)
            where += $" AND ({clause})";

        // Author-defined filters are baked into the form and are not something a visitor can change, they are applied before the search box narrows anything further.
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
            string? match = null;
            if (searchFields.Count == 0 && RecordSearch.MatchExpression(table.Id, term) is { } expression && await RecordSearch.AvailableAsync(db))
                match = expression;

            args.Add(match ?? $"%{EscapeLike(term)}%");
            if (searchFields.Count > 0)
                // Restricted search: only the columns the form exposes.
                search = " AND (" + string.Join(" OR ", searchFields.Select(f => $"{Column(f)} LIKE {{{slot}}} ESCAPE '\\'")) + ")";
            else if (match is not null)
            {
                // Unrestricted search over the fts5 index. Right now it just matches whole words and prefixes, not fragments inside a word, which is the trade for not scanning every record
                search = RecordSearch.Clause("r", slot);
                rankJoin = RecordSearch.RankJoin(slot);
            }
            else
                // No index, or nothing in the term fts5 can tokenize: json_each walks every stored value, a field added later is searchable without a schema change.
                search = $" AND EXISTS (SELECT 1 FROM json_each(r.\"JsonData\") je WHERE je.value LIKE {{{slot}}} ESCAPE '\\')";
        }

        // Counting doubles the work: the same scan again, for a number most callers only render as "page 1 of n".
        var countSql = $"""
            SELECT COUNT(*) AS "Value" FROM (
                SELECT 1 FROM "_records" r WHERE {where}{search} LIMIT {CountCeiling}
            )
            """;
        var total = await db.Database.SqlQueryRaw<int>(countSql, args.ToArray()).SingleAsync();

        // A caller that named a sort field asked for that order and gets it; relevance only fills in for the default, where "newest first" says nothing about a search term.
        var ranked = rankJoin is not null && sortField is null;
        var order = ranked
            ? "m.\"Rank\""
            : sortField is null
                ? "r.\"CreatedAt\""
                : Column(sortField);
        var direction = ranked || !sortDescending ? "ASC" : "DESC";

        var pageSql = $$"""
            SELECT r."Id", r."TableId", r."JsonData", r."CreatedAt", r."UpdatedAt"
            FROM "_records" r{{(ranked ? rankJoin : "")}}
            WHERE {{where}}{{(ranked ? "" : search)}}
            ORDER BY {{order}} {{direction}}, r."Id" DESC
            LIMIT {{pageSize + 1}} OFFSET {{(page - 1) * pageSize}}
            """;
        var records = await db.Records.FromSqlRaw(pageSql, args.ToArray()).AsNoTracking().ToListAsync();

        // One row past the page is what tells a pager there is a next page.
        var hasMore = records.Count > pageSize;
        if (hasMore) records.RemoveAt(records.Count - 1);
        return new ListPage(records, total, page, pageSize, hasMore);
    }

    // Projects a record down to the fields a public form is allowed to reveal.
    public static JsonObject Project(Record record, IReadOnlyList<FieldDefinition> visible)
    {
        var source = JsonNode.Parse(string.IsNullOrWhiteSpace(record.JsonData) ? "{}" : record.JsonData) as JsonObject ?? new JsonObject();
        var result = new JsonObject();
        foreach (var f in visible)
            result[f.Name] = source.TryGetPropertyValue(f.Name, out var v) ? v?.DeepClone() : null;
        return result;
    }

    // Prefers the indexed generated column RecordIndexes maintains for this field; falls back to json_extract for the types that get no column, which is the same scan as before instead of a wrong answer.
    private static string Column(FieldDefinition field) =>
        RecordIndexes.ColumnFor(field) is { } column
            ? $"r.\"{column}\""
            : JsonPath(field.Name);

    // Access rules read the JSON directly instead of the generated column: that column only exists once RecordIndexes has synced the field, and a rule that 500s on an unsynced table is worse than one that scans.
    internal static string JsonPathFor(string fieldName, string alias) => JsonPath(fieldName, alias);

    private static string JsonPath(string fieldName, string alias = "r") =>
        $"json_extract({alias}.\"JsonData\", '$.\"{fieldName.Replace("'", "''").Replace("\"", "\"\"")}\"')";

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // Resolves stored field names (from a form's ConfigJson) to live fields, dropping anything stale.
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
