using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Baseport.Tests;

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
    public async Task RuleSeesRole()
    {
        var (table, fields) = await NotesAsync(createRule: "_USER_.role = 'consumer'");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, new AccessCaller("acct-1", "consumer", null)));
        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, new AccessCaller("acct-2", "user", null)));
    }

    [Fact]
    public void UserRoleIsValid()
    {
        Assert.Null(RecordAccess.Problem("_USER_.role = 'admin'", new List<FieldDefinition>()));
    }

    [Fact]
    public void OtherUserFieldRefused()
    {
        Assert.NotNull(RecordAccess.Problem("_USER_.email = 'x'", new List<FieldDefinition>()));
    }

    [Fact]
    public async Task NoRuleIsOpen()
    {
        var (table, fields) = await NotesAsync();
        var id = await RecordAsync(table, "alice", "hello");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, new AccessCaller("bob", null, null), id));
    }

    [Fact]
    public async Task ReadRuleIsolatesUsers()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");
        var id = await RecordAsync(table, "alice", "hello");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, new AccessCaller("alice", null, null), id));
        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, new AccessCaller("bob", null, null), id));
    }

    [Fact]
    public async Task AnonymousFailsUserRule()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");
        var id = await RecordAsync(table, "alice", "hello");

        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, default, id));
    }

    [Fact]
    public async Task MissingRecordRefuses()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");

        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, new AccessCaller("alice", null, null), "nosuchrecord"));
    }

    [Fact]
    public async Task CreateRuleReadsRequest()
    {
        var (table, fields) = await NotesAsync(createRule: "_REQ_.owner = _USER_.id");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, new AccessCaller("alice", null, null),
            request: new JsonObject { ["owner"] = "alice" }));
        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, new AccessCaller("alice", null, null),
            request: new JsonObject { ["owner"] = "bob" }));
    }

    [Fact]
    public async Task CreateRuleWithRowEvaluates()
    {
        var (table, fields) = await NotesAsync(createRule: "_ROW_.owner IS NULL");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, new AccessCaller("alice", null, null),
            request: new JsonObject { ["owner"] = "alice" }));
    }

    [Fact]
    public async Task RuleEvaluatesEventPayload()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");

        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, new AccessCaller("alice", null, null),
            row: new JsonObject { ["owner"] = "alice" }));
        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, new AccessCaller("bob", null, null),
            row: new JsonObject { ["owner"] = "alice" }));
    }

    [Fact]
    public async Task ReadRuleFiltersList()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");
        await RecordAsync(table, "alice", "first");
        await RecordAsync(table, "bob", "second");
        await RecordAsync(table, "alice", "third");

        var mine = await QueryEngine.ListAsync(_db, table, [], null, true, null, 1, 50,
            accessFields: fields, access: new AccessCaller("alice", null, null));
        Assert.Equal(2, mine.Records.Count);

        var nobody = await QueryEngine.ListAsync(_db, table, [], null, true, null, 1, 50,
            accessFields: fields, access: new AccessCaller("carol", null, null));
        Assert.Empty(nobody.Records);
    }

    [Fact]
    public async Task RoleRuleFiltersRest()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.role = 'consumer'");
        await RecordAsync(table, "alice", "first");
        await RecordAsync(table, "bob", "second");

        var service = await QueryEngine.ListAsync(_db, table, [], null, true, null, 1, 50,
            accessFields: fields, access: new AccessCaller("svc", AccountRoles.Consumer, null));
        var endUser = await QueryEngine.ListAsync(_db, table, [], null, true, null, 1, 50,
            accessFields: fields, access: new AccessCaller("alice", AccountRoles.User, null));

        Assert.Equal(2, service.Records.Count);
        Assert.Empty(endUser.Records);
    }

    [Theory]
    [InlineData(AccountRoles.Consumer, "2")]
    [InlineData(AccountRoles.User, "0")]
    public async Task RoleRuleFiltersWire(string role, string expected)
    {
        var (table, _) = await NotesAsync(readRule: "_USER_.role = 'consumer'");
        await RecordAsync(table, "alice", "first");
        await RecordAsync(table, "bob", "second");

        var result = await SqlEngine.ReadAsync(_db, "SELECT count(*) FROM \"Notes\"",
            conn => WireCatalog.Apply(conn, WireDialect.Postgres, new UserAccount { Id = "caller", Role = role }));

        Assert.Null(result.Error);
        Assert.Equal(expected, Assert.Single(result.Rows)[0]);
    }

    [Fact]
    public async Task FilteredCountMatches()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");
        await RecordAsync(table, "alice", "first");
        await RecordAsync(table, "bob", "second");

        var mine = await QueryEngine.ListAsync(_db, table, [], null, true, null, 1, 50,
            accessFields: fields, access: new AccessCaller("alice", null, null));
        Assert.Equal(1, mine.Total);
    }

    [Fact]
    public async Task SearchCannotWidenRule()
    {
        var (table, fields) = await NotesAsync(readRule: "_USER_.id = _ROW_.owner");
        await RecordAsync(table, "alice", "secret");
        await RecordAsync(table, "bob", "secret");

        var found = await QueryEngine.ListAsync(_db, table, [], null, true, "secret", 1, 50,
            accessFields: fields, access: new AccessCaller("alice", null, null));
        Assert.Single(found.Records);
    }

    [Theory]
    [InlineData("_USER_.id = _ROW_.nosuchfield", "does not name a field")]
    [InlineData("_USER_.name = 'alice'", "Only _USER_.id")]
    [InlineData("1=1; DROP TABLE _records", "single expression")]
    [InlineData("_OTHER_.id = 1", "not one of")]
    public async Task BrokenRuleRefused(string rule, string expected)
    {
        var (_, fields) = await NotesAsync();
        Assert.Contains(expected, RecordAccess.Problem(rule, fields));
    }

    [Fact]
    public async Task UnparseableRuleRefused()
    {
        var (table, fields) = await NotesAsync();
        Assert.Null(await RecordAccess.SqlProblemAsync(_db, table, fields, "_USER_.id = _ROW_.owner"));
        Assert.NotNull(await RecordAccess.SqlProblemAsync(_db, table, fields, "_USER_.id = = _ROW_.owner"));
    }

    [Fact]
    public void RuleMayQuoteField()
    {
        var fields = new List<FieldDefinition> { new() { Name = "user id", DataType = "text" } };
        Assert.Null(RecordAccess.Problem("_ROW_.\"user id\" = _USER_.id", fields));
    }

    [Theory]
    [InlineData("readRule", "1); SELECT (1")]
    [InlineData("createRule", "_ROW_.missing = 1")]
    [InlineData("updateRule", "_USER_.id = ")]
    [InlineData("deleteRule", "_OTHER_.id = 1")]
    public async Task CreateValidatesRules(string key, string rule)
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var table = new TableDefinition
        {
            Name = "Drafts",
            Fields = [new FieldDefinition { Name = "owner", DataType = "text" }]
        };
        RecordAccess.Assign(table, RecordAccess.RuleKeys.Single(k => k.Key == key).Permission, rule);

        Assert.NotEmpty(await TableEndpoints.CreateProblemsAsync(_db, table));
    }

    [Fact]
    public async Task CreateAcceptsValidRule()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var table = new TableDefinition
        {
            Name = "Drafts",
            ReadRule = "_ROW_.owner = _USER_.id",
            Fields = [new FieldDefinition { Name = "owner", DataType = "text" }]
        };

        Assert.Empty(await TableEndpoints.CreateProblemsAsync(_db, table));
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
    public void NoAdminAddressNoFiltering()
    {
        Assert.Null(AdminSurface.Configure(""));
        Assert.Null(AdminSurface.Port);
    }

    [Fact]
    public void BareHostIsHttp()
    {
        Assert.Equal("http://127.0.0.1:5264", AdminSurface.Configure("127.0.0.1:5264"));
        Assert.Equal(5264, AdminSurface.Port);
        AdminSurface.Configure("");
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000", "http://0.0.0.0:5000", true)]
    [InlineData("http://127.0.0.1:5000", "http://*:5000", true)]
    [InlineData("http://127.0.0.1:5000", "http://localhost:5000;http://[::]:6000", true)]
    [InlineData("http://127.0.0.1:5264", "http://0.0.0.0:5000", false)]
    [InlineData("http://127.0.0.1:5264", "http://+:5000;https://localhost:5001", false)]
    public void AdminPortMustDifferFromPublicPorts(string admin, string urls, bool conflict)
    {
        var message = AdminSurface.Conflict(admin, urls.Split(';'));

        Assert.Equal(conflict, message is not null);
        if (conflict)
        {
            Assert.StartsWith("Baseport:AdminAddress", message);
            Assert.Equal(message, StartupFailure.Describe(new InvalidOperationException(message)));
        }
    }

    [Fact]
    public void InvalidAdminAddressIsOneLine()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AdminSurface.Configure("not an address"));
        AdminSurface.Configure("");

        Assert.Equal(ex.Message, StartupFailure.Describe(ex));
    }

    [Fact]
    public void BadAdminAddressFailsStart()
    {
        Assert.Throws<InvalidOperationException>(() => AdminSurface.Configure("not an address"));
        AdminSurface.Configure("");
    }

    [Fact]
    public void ConsoleLinkPerAddress()
    {
        var urls = AdminSurface.ConsoleUrls(["http://0.0.0.0:5000", "http://[::]:5001", "http://127.0.0.1:5002"], null);

        Assert.Equal(["http://localhost:5000/_/admin", "http://localhost:5001/_/admin", "http://127.0.0.1:5002/_/admin"], urls);
    }

    [Fact]
    public void AdminPortIsConsole()
    {
        var urls = AdminSurface.ConsoleUrls(["http://[::]:5000", "http://127.0.0.1:5264"], 5264);

        Assert.Equal(["http://127.0.0.1:5264/_/admin"], urls);
    }

    [Theory]
    [InlineData("/_/admin", true)]
    [InlineData("/_/auth", true)]
    [InlineData("/api/_admin/tables", true)]
    [InlineData("/api/fragments/tables", true)]
    [InlineData("/api/auth/login", true)]

    [InlineData("/api/auth/v1/login", false)]
    [InlineData("/auth/login", false)]
    [InlineData("/api/v1/notes/records", false)]
    [InlineData("/api/forms/abc/form", false)]
    [InlineData("/api/openapi.json", false)]
    [InlineData("/docs", false)]
    public void OperatorSurfaceMoves(string path, bool isAdmin) =>
        Assert.Equal(isAdmin, AdminSurface.IsAdminPath(path));
}

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
    public async Task LastAdminGuardOnPublicSurface()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var admin = await _db.UserAccounts.SingleAsync(TestContext.Current.CancellationToken);

        Assert.True(await AdminEndpoints.IsLastEnabledAdmin(_db, admin));
        Assert.Equal(0, await _db.UserAccounts.CountAsync(a => a.Id != admin.Id && !a.IsDisabled, TestContext.Current.CancellationToken));

        _db.UserAccounts.Add(new UserAccount { Id = "u9", Username = "jane", Role = AccountRoles.User });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await _db.UserAccounts.CountAsync(a => a.Id != admin.Id && !a.IsDisabled, TestContext.Current.CancellationToken));
        Assert.True(await AdminEndpoints.IsLastEnabledAdmin(_db, admin));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
