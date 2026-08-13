using Microsoft.EntityFrameworkCore;

namespace Baseport;

// a VIRTUAL generated column + index per indexable field, so json_extract queries against the shared _records blob can seek instead of scanning
// EF1002 disabled: this is DDL, every interpolated name/value is server-generated or escaped, never client-supplied
#pragma warning disable EF1002
public static class RecordIndexes
{
    private const string Table = "_records";

    // Types worth an index.
    private static readonly HashSet<string> Indexable =
        new() { "text", "number", "currency", "boolean", "date", "datetime", "select", "reference", "systemid",
                 "email", "phone", "url", "color", "time", "rating", "slug" };

    // Named after the field id, which never changes, so a rename rewrites the expression rather than orphaning a column.
    public static string? ColumnFor(FieldDefinition field) =>
        field.Id.Length > 0 && Indexable.Contains(FieldValidation.NormalizeType(field.DataType) ?? "")
            ? $"g_{field.Id}"
            : null;

    // rough estimate, not a measurement: dbstat isn't compiled into this build, so there's no real per-index page count to read
    private const double BytesPerIndexEntry = 24.0 / 0.75;

    public static long EstimateIndexBytes(TableDefinition table, long recordCount)
    {
        if (table.IsProxy || recordCount <= 0) return 0;
        var indexedFields = table.Fields.Count(f => ColumnFor(f) != null);
        return (long)(indexedFields * recordCount * BytesPerIndexEntry);
    }

    public static async Task SyncAsync(AppDbContext db, TableDefinition table)
    {
        if (table.IsProxy) return; // nothing is stored locally to index

        var existing = await ColumnsAsync(db);
        foreach (var field in table.Fields)
        {
            var column = ColumnFor(field);
            if (column is null)
            {
                if (existing.ContainsKey($"g_{field.Id}")) await DropAsync(db, $"g_{field.Id}");
                continue;
            }

            var expression = Expression(field.Name);
            if (existing.TryGetValue(column, out var current))
            {
                if (current == expression) continue;
                await DropAsync(db, column); // the field was renamed
            }

            await db.Database.ExecuteSqlRawAsync(
                $"""ALTER TABLE "{Table}" ADD COLUMN "{column}" GENERATED ALWAYS AS ({expression}) VIRTUAL""");
            // Id trails the sort key because it is the ORDER BY tiebreaker; an index that stops short of it cannot serve the page and SQLite sorts.
            await db.Database.ExecuteSqlRawAsync(
                $"""CREATE INDEX IF NOT EXISTS "ix_{column}" ON "{Table}" ("TableId", "{column}", "Id")""");
        }
    }

    // Called when a field or a whole table goes away, so a dropped field does not leave a column behind for the 2000-column ceiling to count.
    public static async Task DropAsync(AppDbContext db, string column)
    {
        await db.Database.ExecuteSqlRawAsync($"""DROP INDEX IF EXISTS "ix_{column}" """);
        await db.Database.ExecuteSqlRawAsync($"""ALTER TABLE "{Table}" DROP COLUMN "{column}" """);
    }

    public static async Task DropForAsync(AppDbContext db, IEnumerable<FieldDefinition> fields)
    {
        var existing = await ColumnsAsync(db);
        foreach (var field in fields)
            if (existing.ContainsKey($"g_{field.Id}"))
                await DropAsync(db, $"g_{field.Id}");
    }

    private static string Expression(string fieldName) =>
        $"""json_extract("JsonData", '$."{fieldName.Replace("'", "''").Replace("\"", "\"\"")}"')""";

    // Maps generated column name to the expression behind it, read from the stored DDL: PRAGMA table_info does not report a generated column's source.
    private static async Task<Dictionary<string, string>> ColumnsAsync(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State == System.Data.ConnectionState.Closed;
        if (wasClosed) await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{Table}'";
            var ddl = (await cmd.ExecuteScalarAsync()) as string ?? "";

            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                         ddl, """"g_(?<id>[^"]+)" GENERATED ALWAYS AS \((?<expr>.+?)\) VIRTUAL""""))
                found[$"g_{m.Groups["id"].Value}"] = m.Groups["expr"].Value;
            return found;
        }
        finally
        {
            if (wasClosed) await conn.CloseAsync();
        }
    }
}
#pragma warning restore EF1002
