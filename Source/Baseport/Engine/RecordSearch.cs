using System.Text;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Baseport;

// One fts5 index over every record's top-level JSON values, kept current by triggers on _records rather than by application code: the bulk seeders and any future write path go through SQLite, so a trigger cannot be bypassed the way a RecordEngine hook could.
// EF1002 disabled: this is DDL, trigger bodies and fixed literals, with no interpolated input at all.
#pragma warning disable EF1002
public static class RecordSearch
{
    private const string Index = "_records_fts";

    // Scope includes the owning table so a search stays inside one table without a second index per table: fts5 intersects the scope's posting list with the term's, which is the same narrowing a per-table index would buy for three triggers instead of three per table.
    // It is hex rather than the id itself because the tokenizer splits on the '-' and '_' that Ids.NewShortId emits, and two different ids can split into the same tokens.
    private const string Create = $"""CREATE VIRTUAL TABLE "{Index}" USING fts5("Scope", "Body")""";

    public static string Scope(string tableId) => Convert.ToHexString(Encoding.UTF8.GetBytes(tableId));

    // Invalid JSON is indexed as its own raw text instead of being skipped, so a hand-written row is still findable and the trigger can never abort a write.
    private static string Body(string alias) =>
        $"""
        CASE WHEN json_valid({alias}."JsonData")
             THEN (SELECT group_concat(je."value", ' ') FROM json_each({alias}."JsonData") je)
             ELSE {alias}."JsonData" END
        """;

    private static string Row(string alias) => $"""hex({alias}."TableId"), {Body(alias)}""";

    // Created once, then maintained by the triggers. Rebuildable from _records at any time, so it stays out of the migrations and is (re)created here like the generated-column indexes next to it. A definition from an older build is dropped rather than reused, because its columns no longer answer the query this one builds.
    public static async Task EnsureAsync(AppDbContext db)
    {
        if (await DdlAsync(db) == Create) return;

        // All of it or none of it: an index built without its triggers would look present to every later search and answer from a snapshot that stops updating.
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
            // Delete then insert rather than update: a row written before this index existed has no entry to update, and an update that matched nothing would leave it permanently unsearchable.
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
            // A SQLite built without fts5 lands here. Search stays on the LIKE scan it used before, which is slower but correct, so this is a warning and not a failed start.
            await tx.RollbackAsync();
            Log.Warning("Full text search index unavailable, falling back to scanning search: {Error}", ex.Message);
        }
    }

    // Weekly upkeep. A regular fts5 table cannot be rebuilt in place the way an external-content one can, so a drifted index is emptied and refilled from _records.
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

    // ponytail: one sqlite_master read per unrestricted search, rather than a cached flag. A flag would be process-wide over a per-database fact, and the read costs nothing next to the count and page scans around it.
    public static async Task<bool> AvailableAsync(AppDbContext db) => await DdlAsync(db) == Create;

    public static string Clause(string alias, int slot) =>
        $" AND {alias}.\"rowid\" IN (SELECT \"rowid\" FROM \"{Index}\" WHERE \"{Index}\" MATCH {{{slot}}})";

    // Relevance instead of the IN clause, for the page query only: the count does not need an ordering and would pay for one. Scope is weighted out, so a row is ranked on what it says and not on which table it is in.
    public static string RankJoin(int slot) =>
        $" JOIN (SELECT \"rowid\" AS \"Match\", bm25(\"{Index}\", 0.0, 1.0) AS \"Rank\" FROM \"{Index}\" WHERE \"{Index}\" MATCH {{{slot}}}) m ON m.\"Match\" = r.\"rowid\"";

    // Every term becomes a quoted prefix phrase inside the Body column filter, so nothing a visitor types is read as fts5 query syntax. Null when no term survives, which is the caller's signal to fall back to the LIKE scan: fts5 cannot match inside a word, so a search for punctuation or for a fragment of one is better answered slowly than not at all.
    public static string? MatchExpression(string tableId, string query)
    {
        var terms = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            // fts5 parses this string as its own query language, and it scans it as a c string: a NUL inside a term ends the scan mid-token and leaves the opening quote unterminated, which came back as a 500 on a public route. Doubling the quote escapes the only character with meaning here; a control character has none, so it is dropped rather than escaped.
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
