using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Baseport.Tests;

// Per-record access rules, the author writes a SQLite boolean expression over _USER_, _ROW_ and _REQ_ and SQLite decides.
public class RecordAccessTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RecordAccessTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
    }

    private async Task<(TableDefinition Table, List<FieldDefinition> Fields)> NotesAsync(string readRule = "", string createRule = "")
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var table = new TableDefinition
        {
            Id = Ids.NewShortId(12),
            Name = "Notes",
            ApiName = "notes",
            ApiEnabled = true,
            ReadRule = readRule,
            CreateRule = createRule
        };
        var fields = new List<FieldDefinition>
        {
            new() { Id = Ids.NewShortId(12), TableId = table.Id, Name = "owner", DataType = "text", Position = 0 },
            new() { Id = Ids.NewShortId(12), TableId = table.Id, Name = "body", DataType = "text", Position = 1 }
        };
        _db.Tables.Add(table);
        _db.Fields.AddRange(fields);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (table, fields);
    }

    private async Task<string> RecordAsync(TableDefinition table, string owner, string body)
    {
        var record = new Record
        {
            Id = Ids.NewShortId(12),
            TableId = table.Id,
            JsonData = new JsonObject { ["owner"] = owner, ["body"] = body }.ToJsonString(),
            CreatedAt = DateTime.UtcNow
        };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return record.Id;
    }

    [Fact]
    public async Task A_table_with_no_rule_is_open_to_every_caller_the_switches_let_through()
    {
        var (table, fields) = await NotesAsync();
        var id = await RecordAsync(table, "alice", "hello");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, "bob", id));
    }

    // The rule that motivates the whole feature: publishing a table stops publishing it to every signed-in user.
    [Fact]
    public async Task A_read_rule_keeps_one_user_out_of_anothers_record()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");
        var id = await RecordAsync(table, "alice", "hello");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, "alice", id));
        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, "bob", id));
    }

    [Fact]
    public async Task An_anonymous_caller_fails_a_rule_that_names_a_user()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");
        var id = await RecordAsync(table, "alice", "hello");

        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, null, id));
    }

    // A missing row is a refusal, not an error
    [Fact]
    public async Task A_rule_over_a_record_that_is_gone_refuses()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");

        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, "alice", "nosuchrecord"));
    }

    [Fact]
    public async Task A_create_rule_reads_the_submitted_fields_through_REQ()
    {
        var (table, fields) = await NotesAsync(createRule: "_REQ_.owner = _USER_.id");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, "alice",
            request: new JsonObject { ["owner"] = "alice" }));
        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, "alice",
            request: new JsonObject { ["owner"] = "bob" }));
    }

    // A create rule has no row to read, _ROW_ resolves to NULL instead of failing to compile.
    [Fact]
    public async Task A_create_rule_that_mentions_ROW_still_evaluates()
    {
        var (table, fields) = await NotesAsync(createRule: "_ROW_.owner IS NULL");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, "alice",
            request: new JsonObject { ["owner"] = "alice" }));
    }

    // The subscription path has no row to query: a delete event's row is already gone, the rule runs against the payload.
    [Fact]
    public async Task A_rule_can_be_evaluated_against_an_event_payload()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, "alice",
            row: new JsonObject { ["owner"] = "alice" }));
        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, "bob",
            row: new JsonObject { ["owner"] = "alice" }));
    }

    // Listing filters instead of refusing, a caller sees their own rows instead of a 403 (records/list_records.rs:251).
    [Fact]
    public async Task A_read_rule_filters_a_listing_rather_than_refusing_it()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");
        await RecordAsync(table, "alice", "first");
        await RecordAsync(table, "bob", "second");
        await RecordAsync(table, "alice", "third");

        var mine = await QueryEngine.ListAsync(_db, table, [], null, true, null, 1, 50,
            accessFields: fields, accessUserId: "alice");
        Assert.Equal(2, mine.Records.Count);

        var nobody = await QueryEngine.ListAsync(_db, table, [], null, true, null, 1, 50,
            accessFields: fields, accessUserId: "carol");
        Assert.Empty(nobody.Records);
    }

    [Fact]
    public async Task A_filtered_listing_counts_only_what_it_returns()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");
        await RecordAsync(table, "alice", "first");
        await RecordAsync(table, "bob", "second");

        var mine = await QueryEngine.ListAsync(_db, table, [], null, true, null, 1, 50,
            accessFields: fields, accessUserId: "alice");
        Assert.Equal(1, mine.Total);
    }

    // Search runs on top of the rule, never instead of it.
    [Fact]
    public async Task A_search_term_cannot_widen_what_the_rule_allows()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");
        await RecordAsync(table, "alice", "secret");
        await RecordAsync(table, "bob", "secret");

        var found = await QueryEngine.ListAsync(_db, table, [], null, true, "secret", 1, 50,
            accessFields: fields, accessUserId: "alice");
        Assert.Single(found.Records);
    }

    [Theory]
    [InlineData("_USER_.id = _ROW_.nosuchfield", "does not name a field")]
    [InlineData("_USER_.name = 'alice'", "Only _USER_.id")]
    [InlineData("1=1; DROP TABLE _records", "single expression")]
    [InlineData("_OTHER_.id = 1", "not one of")]
    public async Task A_rule_that_could_not_work_is_refused_when_it_is_saved(string rule, string expected)
    {
        var (_, fields) = await NotesAsync();
        Assert.Contains(expected, RecordAccess.Problem(rule, fields));
    }

    [Fact]
    public async Task A_rule_SQLite_cannot_parse_is_refused_when_it_is_saved()
    {
        var (table, fields) = await NotesAsync();
        Assert.Null(await RecordAccess.SqlProblemAsync(_db, table, fields, "_USER_.id = _ROW_.owner"));
        Assert.NotNull(await RecordAccess.SqlProblemAsync(_db, table, fields, "_USER_.id = = _ROW_.owner"));
    }

    [Fact]
    public void A_rule_may_quote_a_field_name()
    {
        var fields = new List<FieldDefinition> { new() { Name = "user id", DataType = "text" } };
        Assert.Null(RecordAccess.Problem("_ROW_.\"user id\" = _USER_.id", fields));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}

public class AdminSurfaceTests
{
    [Fact]
    public void An_unset_admin_address_leaves_one_port_and_no_filtering()
    {
        Assert.Null(AdminSurface.Configure(""));
        Assert.Null(AdminSurface.Port);
    }

    [Fact]
    public void A_bare_host_and_port_is_read_as_an_http_address()
    {
        Assert.Equal("http://127.0.0.1:5264", AdminSurface.Configure("127.0.0.1:5264"));
        Assert.Equal(5264, AdminSurface.Port);
        AdminSurface.Configure("");
    }

    [Fact]
    public void A_nonsense_admin_address_fails_the_start_rather_than_binding_nothing()
    {
        Assert.Throws<InvalidOperationException>(() => AdminSurface.Configure("not an address"));
        AdminSurface.Configure("");
    }

    [Theory]
    [InlineData("/_/admin", true)]
    [InlineData("/_/auth", true)]
    [InlineData("/api/_admin/tables", true)]
    [InlineData("/api/fragments/tables", true)]
    [InlineData("/api/auth/login", true)]
    // The public surface stays on the public port, and /api/auth/v1 is checked before /api/auth.
    [InlineData("/api/auth/v1/login", false)]
    [InlineData("/auth/login", false)]
    [InlineData("/api/v1/notes/records", false)]
    [InlineData("/api/forms/abc/form", false)]
    [InlineData("/api/openapi.json", false)]
    [InlineData("/docs", false)]
    public void The_operator_surface_is_the_part_that_moves(string path, bool isAdmin) =>
        Assert.Equal(isAdmin, AdminSurface.IsAdminPath(path));
}

// The public surface and the console share one session, the routes that delete or disable an account have to enforce the same floor. Dropping the role filter on /api/auth/v1 is what made this reachable.
public class PublicAccountGuardTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public PublicAccountGuardTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
    }

    [Fact]
    public async Task The_last_enabled_admin_is_still_the_last_enabled_admin_on_the_public_surface()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var admin = await _db.UserAccounts.SingleAsync(TestContext.Current.CancellationToken);

        Assert.True(await AdminEndpoints.IsLastEnabledAdmin(_db, admin));
        Assert.Equal(0, await _db.UserAccounts.CountAsync(a => a.Id != admin.Id && !a.IsDisabled, TestContext.Current.CancellationToken));

        _db.UserAccounts.Add(new UserAccount { Id = "u9", Username = "jane", Role = AccountRoles.User });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Another enabled account exists now, but it is not an admin, the admin floor still holds.
        Assert.Equal(1, await _db.UserAccounts.CountAsync(a => a.Id != admin.Id && !a.IsDisabled, TestContext.Current.CancellationToken));
        Assert.True(await AdminEndpoints.IsLastEnabledAdmin(_db, admin));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
