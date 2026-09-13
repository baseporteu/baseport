using Microsoft.EntityFrameworkCore;

namespace Baseport;

#pragma warning disable EF1002
public static class RecordIndexes
{
    private const string Table = "_records";

    public static string? ColumnFor(FieldDefinition field) =>
        field.Id.Length > 0 && FieldTypes.Of(field).Indexable ? $"g_{field.Id}" : null;

    private const double BytesPerIndexEntry = 24.0 / 0.75;

    public static long EstimateIndexBytes(TableDefinition table, long recordCount)
    {
        if (table.IsProxy || recordCount <= 0) return 0;
        var indexes = table.Fields.Count(f => ColumnFor(f) != null) + table.Fields.Count(f => f.IsUnique && ColumnFor(f) != null);
        return (long)(indexes * recordCount * BytesPerIndexEntry);
    }

    public static async Task SyncAsync(AppDbContext db, TableDefinition table)
    {
        if (table.IsProxy) return;

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
                if (current != expression)
                {
                    await DropAsync(db, column);
                    await CreateColumnAndIndexAsync(db, column, expression);
                }
            }
            else
            {
                await CreateColumnAndIndexAsync(db, column, expression);
            }

            await SyncUniqueIndexAsync(db, column, field.IsUnique);
        }
    }

    private static async Task CreateColumnAndIndexAsync(AppDbContext db, string column, string expression)
    {
        await db.Database.ExecuteSqlRawAsync(
            $"""ALTER TABLE "{Table}" ADD COLUMN "{column}" GENERATED ALWAYS AS ({expression}) VIRTUAL""");

        await db.Database.ExecuteSqlRawAsync(
            $"""CREATE INDEX IF NOT EXISTS "ix_{column}" ON "{Table}" ("TableId", "{column}", "Id")""");
    }

    private static async Task SyncUniqueIndexAsync(AppDbContext db, string column, bool isUnique)
    {
        if (isUnique)
            await db.Database.ExecuteSqlRawAsync(
                $"""CREATE INDEX IF NOT EXISTS "ix_{column}_ci" ON "{Table}" ("TableId", "{column}" COLLATE NOCASE, "Id")""");
        else
            await db.Database.ExecuteSqlRawAsync($"""DROP INDEX IF EXISTS "ix_{column}_ci" """);
    }

    public static async Task DropAsync(AppDbContext db, string column)
    {
        await db.Database.ExecuteSqlRawAsync($"""DROP INDEX IF EXISTS "ix_{column}" """);
        await db.Database.ExecuteSqlRawAsync($"""DROP INDEX IF EXISTS "ix_{column}_ci" """);
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
