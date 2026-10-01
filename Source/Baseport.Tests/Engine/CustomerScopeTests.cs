using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baseport.Tests;

[Collection(nameof(RecordEvents))]
public class CustomerScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public CustomerScopeTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UserAccount Account(string scope) =>
        new() { Id = Ids.NewShortId(12), Role = AccountRoles.Consumer, Scope = scope };

    private async Task<(TableDefinition Table, List<FieldDefinition> Fields)> OrdersAsync(string scopeField = "client_id", string readRule = "")
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var table = new TableDefinition
        {
            Id = Ids.NewShortId(12),
            Name = "Orders",
            ApiName = "orders",
            ApiEnabled = true,
            ScopeField = scopeField,
            ReadRule = readRule
        };
        var fields = new List<FieldDefinition>
        {
            new() { Id = Ids.NewShortId(12), TableId = table.Id, Name = "client_id", DataType = "text", Position = 0 },
            new() { Id = Ids.NewShortId(12), TableId = table.Id, Name = "body", DataType = "text", Position = 1 }
        };
        _db.Tables.Add(table);
        _db.Fields.AddRange(fields);
        await _db.SaveChangesAsync(Ct);
        return (table, fields);
    }

    private async Task<string> OrderAsync(TableDefinition table, string clientId, string body)
    {
        var record = new Record
        {
            Id = Ids.NewShortId(12),
            TableId = table.Id,
            JsonData = new JsonObject { ["client_id"] = clientId, ["body"] = body }.ToJsonString(),
            CreatedAt = DateTime.UtcNow
        };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(Ct);
        return record.Id;
    }

    private Task<QueryEngine.ListPage> ListAsync(TableDefinition table, List<FieldDefinition> fields, UserAccount caller) =>
        QueryEngine.ListAsync(_db, table, fields, null, false, null, 1, 25, accessFields: fields, access: AccessCaller.Of(caller));

    [Fact]
    public async Task ListShowsOnlyOwnCustomer()
    {
        var (table, fields) = await OrdersAsync();
        await OrderAsync(table, "ACME", "a1");
        await OrderAsync(table, "ACME", "a2");
        await OrderAsync(table, "GLOBEX", "g1");

        var page = await ListAsync(table, fields, Account("ACME"));

        Assert.Equal(2, page.Total);
        Assert.All(page.Records, r => Assert.Contains("\"ACME\"", r.JsonData));
    }

    [Fact]
    public async Task OtherCustomerRowRefusedForReadUpdateDelete()
    {
        var (table, fields) = await OrdersAsync();
        var globex = await OrderAsync(table, "GLOBEX", "g1");
        var acme = AccessCaller.Of(Account("ACME"));

        foreach (var permission in new[] { Permission.Read, Permission.Update, Permission.Delete })
            Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, permission, acme, globex));
        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, AccessCaller.Of(Account("GLOBEX")), globex));
    }

    [Fact]
    public async Task CreateStampsCallerScopeOverForgedValue()
    {
        var (table, fields) = await OrdersAsync();
        var caller = Account("ACME");
        var obj = new JsonObject { ["client_id"] = "GLOBEX", ["body"] = "x" };

        var outcome = await RecordEngine.PrepareAsync(_db, table, fields, obj, scope: AccessCaller.Of(caller).Scope);

        Assert.False(outcome.HasErrors);
        Assert.Equal("ACME", obj["client_id"]!.GetValue<string>());
        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, AccessCaller.Of(caller), request: obj));
    }

    [Fact]
    public async Task UnstampedCreateIsRefused()
    {
        var (table, fields) = await OrdersAsync();
        var obj = new JsonObject { ["client_id"] = "GLOBEX", ["body"] = "x" };

        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, AccessCaller.Of(Account("ACME")), request: obj));
    }

    [Fact]
    public async Task UpdateCannotMoveRowToAnotherCustomer()
    {
        var (table, fields) = await OrdersAsync();
        var id = await OrderAsync(table, "ACME", "a1");
        var record = await _db.Records.SingleAsync(r => r.Id == id, Ct);

        var (merged, outcome) = await RecordEngine.ApplyUpdateAsync(_db, table, fields, record,
            new JsonObject { ["client_id"] = "GLOBEX" }, replace: false, AccessCaller.Of(Account("ACME")).Scope);

        Assert.False(outcome.HasErrors);
        Assert.Equal("ACME", merged["client_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnscopedAccountIsRefused()
    {
        var (table, fields) = await OrdersAsync();
        var id = await OrderAsync(table, "ACME", "a1");
        var unscoped = Account("");

        Assert.Equal(0, (await ListAsync(table, fields, unscoped)).Total);
        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, AccessCaller.Of(unscoped), id));
        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Create, AccessCaller.Of(unscoped),
            request: new JsonObject { ["client_id"] = "", ["body"] = "x" }));
    }

    [Fact]
    public async Task UnscopedTableIsUnaffected()
    {
        var (table, fields) = await OrdersAsync(scopeField: "");
        await OrderAsync(table, "ACME", "a1");
        await OrderAsync(table, "GLOBEX", "g1");

        Assert.Equal(2, (await ListAsync(table, fields, Account(""))).Total);
    }

    [Fact]
    public async Task ScopeAddsToAuthorRule()
    {
        var (table, fields) = await OrdersAsync(readRule: "_ROW_.body <> 'hidden'");
        await OrderAsync(table, "ACME", "a1");
        await OrderAsync(table, "ACME", "hidden");
        await OrderAsync(table, "GLOBEX", "g1");

        Assert.Equal(1, (await ListAsync(table, fields, Account("ACME"))).Total);
    }

    [Fact]
    public async Task SubscriptionRowFromOtherCustomerIsRefused()
    {
        var (table, fields) = await OrdersAsync();
        var row = new JsonObject { ["client_id"] = "GLOBEX", ["body"] = "g1" };

        Assert.False(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, AccessCaller.Of(Account("ACME")), row: row));
        Assert.True(await RecordAccess.AllowsAsync(_db, table, fields, Permission.Read, AccessCaller.Of(Account("GLOBEX")), row: row));
    }

    [Fact]
    public async Task TransactionCannotTouchOtherCustomer()
    {
        var (table, _) = await OrdersAsync();
        var globex = await OrderAsync(table, "GLOBEX", "g1");
        var acme = Account("ACME");

        var update = await RecordTransactions.ExecuteAsync(_db,
            [new("update", "orders", globex, new JsonObject { ["body"] = "taken" })], transactional: true, acme, Ct);
        var delete = await RecordTransactions.ExecuteAsync(_db,
            [new("delete", "orders", globex, null)], transactional: true, acme, Ct);
        var create = await RecordTransactions.ExecuteAsync(_db,
            [new("create", "orders", null, new JsonObject { ["client_id"] = "GLOBEX", ["body"] = "mine" })], transactional: true, acme, Ct);

        Assert.Equal(ApiProblem.Forbidden, update.Problem);
        Assert.Equal(ApiProblem.Forbidden, delete.Problem);
        Assert.Null(create.Problem);
        var created = await _db.Records.AsNoTracking().SingleAsync(r => r.Id == create.Ids[0], Ct);
        Assert.Contains("\"ACME\"", created.JsonData);
        Assert.Contains("g1", (await _db.Records.AsNoTracking().SingleAsync(r => r.Id == globex, Ct)).JsonData);
    }

    [Fact]
    public async Task WireViewShowsOnlyOwnCustomer()
    {
        var (table, _) = await OrdersAsync();
        await OrderAsync(table, "ACME", "a1");
        await OrderAsync(table, "GLOBEX", "g1");

        var result = await SqlEngine.ReadAsync(_db, "SELECT count(*) FROM \"Orders\"",
            conn => WireCatalog.Apply(conn, WireDialect.Postgres, Account("ACME")));

        Assert.Null(result.Error);
        Assert.Equal("1", Assert.Single(result.Rows)[0]);
    }

    [Theory]
    [InlineData("client_id", true)]
    [InlineData("", true)]
    [InlineData("missing", false)]
    public async Task ScopeFieldMustNameAField(string scopeField, bool valid)
    {
        var (_, fields) = await OrdersAsync();

        Assert.Equal(valid, RecordAccess.ScopeProblem(scopeField, fields) is null);
    }

    [Fact]
    public async Task CreatingATableChecksItsScopeField()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        TableDefinition Table(string scopeField, bool proxy = false) => new()
        {
            Name = "Invoices",
            IsProxy = proxy,
            ScopeField = scopeField,
            Fields = [new() { Name = "debtor", DataType = "text" }]
        };

        Assert.Empty(await TableEndpoints.CreateProblemsAsync(_db, Table("debtor")));
        Assert.NotEmpty(await TableEndpoints.CreateProblemsAsync(_db, Table("missing")));
        Assert.NotEmpty(await TableEndpoints.CreateProblemsAsync(_db, Table("debtor", proxy: true)));
    }

    [Fact]
    public void ScopeFieldMustBeTextLike() =>
        Assert.NotNull(RecordAccess.ScopeProblem("total", [new() { Name = "total", DataType = "number" }]));

    [Theory]
    [InlineData("ACME", true)]
    [InlineData("", true)]
    [InlineData("a\u0000b", false)]
    public void AccountScopeIsValidated(string scope, bool valid) =>
        Assert.Equal(valid, AccountValidation.ScopeProblem(scope) is null);

    [Fact]
    public void AccountScopeHasALengthCap() =>
        Assert.NotNull(AccountValidation.ScopeProblem(new string('x', AccountValidation.ScopeMax + 1)));

    [Fact]
    public void RulesMayReferToUserScope() =>
        Assert.Null(RecordAccess.Problem("_ROW_.client_id = _USER_.scope", [new() { Name = "client_id" }]));
}
