using System.Text;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Baseport;

#pragma warning disable EF1002
public static class RecordSearch
{
    private const string Index = "_records_fts";

    private const string Create = $"""CREATE VIRTUAL TABLE "{Index}" USING fts5("Scope", "Body")""";

    public static string Scope(string tableId) => Convert.ToHexString(Encoding.UTF8.GetBytes(tableId));

    private static string Body(string alias) =>
        $"""
        CASE WHEN json_valid({alias}."JsonData")
             THEN (SELECT group_concat(je."value", ' ') FROM json_each({alias}."JsonData") je)
             ELSE {alias}."JsonData" END
        """;

    private static string Row(string alias) => $"""hex({alias}."TableId"), {Body(alias)}""";

    public static async Task EnsureAsync(AppDbContext db)
    {
        if (await DdlAsync(db) == Create) return;

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await DropAsync(db);
            await db.Database.ExecuteSqlRawAsync(Create);
            await FillAsync(db);

            await db.Database.ExecuteSqlRawAsync(
                $"""
                CREATE TRIGGER "trg_{Index}_ai" AFTER INSERT ON "_records" BEGIN
                    INSERT INTO "{Index}"("rowid", "Scope", "Body") VALUES (NEW."rowid", {Row("NEW")});
                END
                """);
            await db.Database.ExecuteSqlRawAsync(
                $"""
                CREATE TRIGGER "trg_{Index}_ad" AFTER DELETE ON "_records" BEGIN
                    DELETE FROM "{Index}" WHERE "rowid" = OLD."rowid";
                END
                """);

            await db.Database.ExecuteSqlRawAsync(
                $"""
                CREATE TRIGGER "trg_{Index}_au" AFTER UPDATE ON "_records" BEGIN
                    DELETE FROM "{Index}" WHERE "rowid" = NEW."rowid";
                    INSERT INTO "{Index}"("rowid", "Scope", "Body") VALUES (NEW."rowid", {Row("NEW")});
                END
                """);
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {

            await tx.RollbackAsync();
            Log.Warning("Full text search index unavailable, falling back to scanning search: {Error}", ex.Message);
        }
    }

    public static async Task<string> MaintainAsync(AppDbContext db, CancellationToken ct)
    {
        await EnsureAsync(db);
        if (await DdlAsync(db) != Create) return "Full text search is unavailable on this SQLite build; search is scanning.";

        var records = await db.Database.SqlQueryRaw<int>("""SELECT COUNT(*) AS "Value" FROM "_records" """).SingleAsync(ct);
        var indexed = await db.Database.SqlQueryRaw<int>($"""SELECT COUNT(*) AS "Value" FROM "{Index}" """).SingleAsync(ct);

        if (records != indexed)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync($"""DELETE FROM "{Index}" """, ct);
            await FillAsync(db, ct);
            await tx.CommitAsync(ct);
            return $"Rebuilt the search index: {indexed} entries for {records} records.";
        }

        await db.Database.ExecuteSqlRawAsync($"""INSERT INTO "{Index}"("{Index}") VALUES('optimize')""", ct);
        return $"Optimized the search index over {records} record(s).";
    }

    public static async Task<bool> AvailableAsync(AppDbContext db) => await DdlAsync(db) == Create;

    public static string Clause(string alias, int slot) =>
        $" AND {alias}.\"rowid\" IN (SELECT \"rowid\" FROM \"{Index}\" WHERE \"{Index}\" MATCH {{{slot}}})";

    public static string RankJoin(int slot) =>
        $" JOIN (SELECT \"rowid\" AS \"Match\", bm25(\"{Index}\", 0.0, 1.0) AS \"Rank\" FROM \"{Index}\" WHERE \"{Index}\" MATCH {{{slot}}}) m ON m.\"Match\" = r.\"rowid\"";

    public static string? MatchExpression(string tableId, string query)
    {
        var terms = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)

            .Select(t => new string(t.Where(c => !char.IsControl(c)).ToArray()))
            .Where(t => t.Any(char.IsLetterOrDigit))
            .Select(t => $"\"{t.Replace("\"", "\"\"")}\"*")
            .ToList();
        return terms.Count > 0
            ? $"{{\"Scope\"}} : \"{Scope(tableId)}\" AND {{\"Body\"}} : ({string.Join(" ", terms)})"
            : null;
    }

    private static Task FillAsync(AppDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            $"""INSERT INTO "{Index}"("rowid", "Scope", "Body") SELECT r."rowid", {Row("r")} FROM "_records" r""", ct);

    private static async Task DropAsync(AppDbContext db)
    {
        foreach (var suffix in new[] { "ai", "ad", "au" })
            await db.Database.ExecuteSqlRawAsync($"""DROP TRIGGER IF EXISTS "trg_{Index}_{suffix}" """);
        await db.Database.ExecuteSqlRawAsync($"""DROP TABLE IF EXISTS "{Index}" """);
    }

    private static async Task<string?> DdlAsync(AppDbContext db) =>
        (await db.Database
            .SqlQueryRaw<string?>($"""SELECT "sql" AS "Value" FROM sqlite_master WHERE "name" = '{Index}'""")
            .ToListAsync())
        .FirstOrDefault();
}
#pragma warning restore EF1002
