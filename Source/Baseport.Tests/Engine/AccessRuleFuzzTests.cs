using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public partial class AccessRuleFuzzTests : IDisposable
{
    private const int Cases = 5000;
    private const string UserId = "o'user\"1";
    private const string Role = "role') OR ('1";

    private static readonly string[] Tokens =
    [
        "_USER_.id", "_USER_.role", "_ROW_.status", "_ROW_.\"owner\"", "_ROW_.amount", "_REQ_.amount", "_REQ_.status",
        "'", "''", "'x'", "'a''b'", "\"", "\"status\"", "[", "]", "`", "(", ")", "(", ")", ";", "--", "/*", "*/",
        "=", "<>", "<", "||", "+", ",", "AND", "OR", "NOT", "IS", "NULL", "IN", "LIKE", "CASE", "WHEN", "THEN", "ELSE", "END",
        "1", "0", "42", "-1", "AS", "UPDATE", "ATTACH", "SELECT", "FROM", "WHERE", "_users", "_records", "EXISTS",
        "json_extract", "abs", "{0}", "{9}", "é", "​", "\n", "_ROW_", "_OTHER_.x", "?", "@p", "$x", ":y"
    ];

    private static readonly List<FieldDefinition> Fields =
        [new() { Name = "status" }, new() { Name = "owner" }, new() { Name = "amount" }];

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly TableDefinition _table = new() { Id = "fuzztable001", Name = "Fuzz", CreatedAt = DateTime.UtcNow };

    public AccessRuleFuzzTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        _db.Database.EnsureCreated();
        _db.Tables.Add(_table);
        _db.Tables.Add(new TableDefinition { Id = "othertable01", Name = "Other", CreatedAt = DateTime.UtcNow });
        _db.Records.Add(new Record { Id = "fuzzrecord01", TableId = _table.Id, JsonData = """{"status":"open","owner":"o'user\"1","amount":3}""", CreatedAt = DateTime.UtcNow });
        _db.Records.Add(new Record { Id = "otherrecord1", TableId = "othertable01", JsonData = """{"secret":"x"}""", CreatedAt = DateTime.UtcNow });
        _db.UserAccounts.Add(new UserAccount { Id = "fuzzuser0001", Username = "fuzz", Role = AccountRoles.Admin, PasswordHash = "h" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    internal static string Generate(Random rng)
    {
        var text = new StringBuilder();
        var count = rng.Next(1, 12);
        for (var i = 0; i < count; i++)
        {
            if (i > 0 && rng.Next(4) != 0) text.Append(' ');
            text.Append(Tokens[rng.Next(Tokens.Length)]);
        }
        return text.ToString();
    }

    private string Hash()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT group_concat(Id || JsonData, '|') FROM _records)
                || (SELECT group_concat(Id || Username || Role || PasswordHash, '|') FROM _users)
                || (SELECT group_concat(Id || ReadRule, '|') FROM _tables)
                || (SELECT count(*) FROM sqlite_master)
            """;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private List<string> Rows(string sql, IReadOnlyList<object?> args)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = SlotToken().Replace(sql, m => "$p" + m.Groups[1].Value);
        for (var i = 0; i < args.Count; i++) cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        var rows = new List<string>();
        using var reader = cmd.ExecuteReader();
        do
        {
            while (reader.Read()) rows.Add(Convert.ToString(reader.GetValue(0)) ?? "");
        } while (reader.NextResult());
        return rows;
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private int TempViews()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM temp.sqlite_master WHERE type = 'view'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // null when the rule holds every invariant
    private async Task<string?> ViolationAsync(string rule)
    {
        string? problem;
        try
        {
            problem = await RecordAccess.RuleProblemAsync(_db, _table, Fields, rule);
        }
        catch (Exception ex)
        {
            return $"the save check threw {ex.GetType().Name}: {ex.Message}";
        }
        if (problem is not null || string.IsNullOrWhiteSpace(rule)) return null;

        try
        {
            var args = new List<object?>();
            var expression = RecordAccess.Rewrite(rule, Fields, "r", UserId,
                new JsonObject { ["amount"] = "1') OR ('1", ["status"] = "x" }, null, args, Role);
            var bound = Rows($"SELECT 1 FROM _records r WHERE 0 AND ({expression}) UNION ALL SELECT 2", args);
            if (bound is not ["2"]) return $"the bound rule escaped its parentheses: {string.Join(",", bound)}";

            var clause = RecordAccess.ReadClauseLiteral(rule, Fields, "r", UserId, Role);
            Exec($"CREATE TEMP VIEW fuzz AS SELECT 1 AS v FROM _records r WHERE 0 AND COALESCE(({clause}), 0) UNION ALL SELECT 2");
            try
            {
                if (TempViews() != 1) return "the inlined rule created more than one view";
                var inlined = Rows("SELECT v FROM fuzz", []);
                if (inlined is not ["2"]) return $"the inlined rule escaped its parentheses: {string.Join(",", inlined)}";
            }
            finally
            {
                foreach (var view in new[] { "fuzz" }) Exec($"DROP VIEW IF EXISTS temp.\"{view}\"");
            }
        }
        catch (SqliteException ex)
        {
            return $"an accepted rule failed to run: {ex.Message}";
        }
        return null;
    }

    [Fact]
    public async Task Accepted_rules_stay_one_expression_and_change_nothing()
    {
        var seed = Random.Shared.Next();
        var rng = new Random(seed);
        var before = Hash();

        for (var i = 0; i < Cases; i++)
        {
            var rule = Generate(rng);
            var violation = await ViolationAsync(rule);
            Assert.True(violation is null, $"seed {seed}, case {i}, rule [{rule}]: {violation}");
        }

        Assert.Equal(before, Hash());
    }

    [Theory]
    [InlineData("0) OR 1 OR (0")]
    [InlineData("_ROW_.status = 'open') OR (1")]
    [InlineData("1 --")]
    [InlineData("1 /*")]
    [InlineData("_USER_.id = '{0}'")]
    [InlineData("{9} = 1")]
    [InlineData("$x = 1")]
    [InlineData("_USER_.id = @p0")]
    [InlineData("'_USER_.id' = 1")]
    public async Task A_rule_that_breaks_out_or_collides_with_a_slot_is_refused(string rule)
    {
        Assert.Null(await ViolationAsync(rule));
        Assert.NotNull(await RecordAccess.RuleProblemAsync(_db, _table, Fields, rule));
    }

    [Theory]
    [InlineData("_USER_.id\u200b")]
    [InlineData("_ROW_.\"owner\" <> + _REQ_.amount\u200b")]
    [InlineData("NOT_REQ_.amount")]
    public async Task A_substituted_value_never_merges_with_the_next_token(string rule) =>
        Assert.Null(await ViolationAsync(rule));

    [Theory]
    [InlineData("_ROW_.owner = _USER_.id")]
    [InlineData("(_ROW_.status = 'a)b' OR _ROW_.\"owner\" = ')(')")]
    [InlineData("_USER_.role IN ('consumer', 'admin')")]
    public async Task Ordinary_rules_with_brackets_in_strings_are_still_accepted(string rule) =>
        Assert.Null(await RecordAccess.RuleProblemAsync(_db, _table, Fields, rule));

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex SlotToken();
}
