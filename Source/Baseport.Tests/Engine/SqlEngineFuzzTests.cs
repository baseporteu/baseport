using System.Text;
using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

[Collection(nameof(WireLimitsTests))]
public class SqlEngineFuzzTests : IDisposable
{
    private const int Cases = 1500;

    private static readonly string[] Starts =
        ["SELECT", "select", " WITH a AS (SELECT 1)", "PRAGMA", "EXPLAIN", "VALUES", "WITH RECURSIVE c(x) AS (SELECT 1)"];

    private static readonly string[] Tokens =
    [
        "1", "*", "FROM", "_users", "_records", "_tables", "sqlite_master", "fuzz", "WHERE", "Id", "=", "'x'", "'", "\"", "(", ")",
        ";", "--", "/*", "*/", "\n", ",", "AS", "UNION", "ALL", "SELECT",
        "DELETE FROM _users", "UPDATE _users SET Role = 'x'", "INSERT INTO _records (Id) VALUES ('z')", "REPLACE INTO _users (Id) VALUES ('y')",
        "DROP TABLE _users", "CREATE TABLE t (x)", "ATTACH 'evil.db' AS evil", "DETACH main", "VACUUM", "VACUUM INTO 'copy.db'",
        "query_only = 0", "query_only = OFF", "user_version = 9", "writable_schema = 1", "journal_mode = DELETE", "main.user_version",
        "temp_store = 2", "load_extension('x')", "randomblob(10)", "json_extract(JsonData, '$.a')", "zeroblob(10)",
        "RETURNING *", "ON CONFLICT DO NOTHING", "é", "​", "?", "$x", "@p", ":y"
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "baseport-sqlfuzz-" + Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;
    private readonly AppDbContext _db;
    private readonly UserAccount _caller = new() { Id = "fuzzcaller01", Username = "caller", Role = AccountRoles.Consumer };
    private readonly TimeSpan _savedDeadline = SqlEngine.StatementDeadline;

    public SqlEngineFuzzTests()
    {
        SqlEngine.StatementDeadline = TimeSpan.FromMilliseconds(100);
        Directory.CreateDirectory(_directory);
        _connectionString = $"Data Source={Path.Combine(_directory, "fuzz.db")}";
        _db = TestDb.Open(_connectionString);
        _db.Database.Migrate();
        _db.Tables.Add(new TableDefinition { Id = "fuzztable001", Name = "fuzz", ApiName = "fuzz", ApiEnabled = true, CreatedAt = DateTime.UtcNow });
        _db.Fields.Add(new FieldDefinition { Id = "fuzzfield001", TableId = "fuzztable001", Name = "a", DataType = "text" });
        _db.Records.Add(new Record { Id = "fuzzrecord01", TableId = "fuzztable001", JsonData = """{"a":"x"}""", CreatedAt = DateTime.UtcNow });
        _db.UserAccounts.Add(_caller);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        SqlEngine.StatementDeadline = _savedDeadline;
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static string Generate(Random rng)
    {
        var text = new StringBuilder(Starts[rng.Next(Starts.Length)]);
        var count = rng.Next(0, 8);
        for (var i = 0; i < count; i++)
        {
            text.Append(rng.Next(5) == 0 ? "" : " ");
            text.Append(Tokens[rng.Next(Tokens.Length)]);
        }
        return text.ToString();
    }

    private string Hash()
    {
        using var conn = new SqliteConnection(_connectionString + ";Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT group_concat(Id || JsonData, '|') FROM _records)
                || (SELECT group_concat(Id || Username || Role, '|') FROM _users)
                || (SELECT group_concat(type || name || ifnull(sql, ''), '|') FROM sqlite_master)
                || (SELECT user_version FROM pragma_user_version)
            """;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    [Fact]
    public async Task Accepted_statements_never_change_the_database()
    {
        var seed = Random.Shared.Next();
        var rng = new Random(seed);
        var before = Hash();
        var accepted = 0;

        for (var i = 0; i < Cases; i++)
        {
            var sql = Generate(rng);
            if (SqlEngine.Validate(sql) is not null) continue;
            accepted++;

            var wire = i % 2 == 0;
            try
            {
                if (wire)
                    await SqlEngine.ReadAsync(_db, sql, conn => WireCatalog.Apply(conn, WireDialect.Postgres, _caller));
                else
                    await SqlEngine.ReadAsync(_db, sql, WireCatalog.Views, restrict: false);
            }
            catch (Exception ex)
            {
                Assert.Fail($"seed {seed}, case {i}, [{sql}]: ReadAsync threw {ex.GetType().Name}: {ex.Message}");
            }

            var after = Hash();
            Assert.True(before == after, $"seed {seed}, case {i}, {(wire ? "wire" : "console")} [{sql}] changed the database");
        }

        Assert.True(accepted > Cases / 4, $"seed {seed}: only {accepted} statements were accepted");
        Assert.False(File.Exists(Path.Combine(_directory, "copy.db")), $"seed {seed}: VACUUM INTO wrote a copy");
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "copy.db")), $"seed {seed}: VACUUM INTO wrote a copy");
    }

    private string JournalMode()
    {
        using var conn = new SqliteConnection(_connectionString + ";Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    [Theory]
    [InlineData("PRAGMA journal_mode = DELETE")]
    [InlineData("PRAGMA journal_mode(MEMORY)")]
    [InlineData("PRAGMA wal_checkpoint(TRUNCATE)")]
    [InlineData("PRAGMA optimize")]
    public async Task A_console_pragma_that_sets_or_maintains_is_refused(string sql)
    {
        var mode = JournalMode();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await SqlEngine.ReadAsync(_db, sql, WireCatalog.Views, restrict: false);

        Assert.NotNull(result.Error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Equal(mode, JournalMode());
    }

    [Theory]
    [InlineData("PRAGMA table_info(_users)")]
    [InlineData("PRAGMA user_version")]
    [InlineData("PRAGMA journal_mode")]
    [InlineData("SELECT * FROM pragma_table_info('_records')")]
    public async Task A_console_pragma_that_reads_still_works(string sql) =>
        Assert.Null((await SqlEngine.ReadAsync(_db, sql, WireCatalog.Views, restrict: false)).Error);
}
