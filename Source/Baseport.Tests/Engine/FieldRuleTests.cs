using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Baseport.Tests;

[Collection(nameof(RecordEvents))]
public class FieldRuleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public FieldRuleTests()
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

    private static readonly AccessCaller Staff = new("staff", AccountRoles.Consumer, null);
    private static readonly AccessCaller Visitor = new("visitor", AccountRoles.User, null);

    private async Task<(TableDefinition Table, List<FieldDefinition> Fields)> PeopleAsync(string salaryRead = "", string salaryWrite = "")
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "People", ApiName = "people", ApiEnabled = true };
        var fields = new List<FieldDefinition>
        {
            new() { Id = Ids.NewShortId(12), TableId = table.Id, Name = "name", DataType = "text", Position = 0 },
            new() { Id = Ids.NewShortId(12), TableId = table.Id, Name = "owner", DataType = "text", Position = 1 },
            new() { Id = Ids.NewShortId(12), TableId = table.Id, Name = "salary", DataType = "number", Position = 2, ReadRule = salaryRead, WriteRule = salaryWrite }
        };
        _db.Tables.Add(table);
        _db.Fields.AddRange(fields);
        await _db.SaveChangesAsync(Ct);
        return (table, fields);
    }

    private async Task<Record> PersonAsync(TableDefinition table, string name, string owner, int salary)
    {
        var record = new Record
        {
            Id = Ids.NewShortId(12),
            TableId = table.Id,
            JsonData = new JsonObject { ["name"] = name, ["owner"] = owner, ["salary"] = salary }.ToJsonString(),
            CreatedAt = DateTime.UtcNow
        };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(Ct);
        return record;
    }

    private static JsonObject Data(Record record) => (JsonObject)JsonNode.Parse(record.JsonData)!;

    [Fact]
    public async Task ReadRuleStripsFieldPerCaller()
    {
        var (table, fields) = await PeopleAsync(salaryRead: "_USER_.role = 'consumer'");
        var record = await PersonAsync(table, "Ann", "staff", 5000);

        var forStaff = await RecordAccess.RedactAsync(_db, table, fields, Staff, [record], Ct);
        var forVisitor = await RecordAccess.RedactAsync(_db, table, fields, Visitor, [record], Ct);

        Assert.True(Data(forStaff[0]).ContainsKey("salary"));
        Assert.False(Data(forVisitor[0]).ContainsKey("salary"));
        Assert.Equal("Ann", Data(forVisitor[0])["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadRuleCanDependOnTheRow()
    {
        var (table, fields) = await PeopleAsync(salaryRead: "_ROW_.owner = _USER_.id");
        var own = await PersonAsync(table, "Ann", "visitor", 5000);
        var other = await PersonAsync(table, "Bob", "staff", 6000);

        var shown = await RecordAccess.RedactAsync(_db, table, fields, Visitor, [own, other], Ct);

        Assert.True(Data(shown[0]).ContainsKey("salary"));
        Assert.False(Data(shown[1]).ContainsKey("salary"));
    }

    [Fact]
    public async Task RedactionNeverTouchesTheStoredRecord()
    {
        var (table, fields) = await PeopleAsync(salaryRead: "0");
        var record = await PersonAsync(table, "Ann", "staff", 5000);

        await RecordAccess.RedactAsync(_db, table, fields, Visitor, [record], Ct);
        await _db.SaveChangesAsync(Ct);

        Assert.Contains("5000", (await _db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct)).JsonData);
    }

    [Fact]
    public async Task EventRowIsRedacted()
    {
        var (table, fields) = await PeopleAsync(salaryRead: "_USER_.role = 'consumer'");
        var row = new JsonObject { ["name"] = "Ann", ["salary"] = 5000 };

        var shown = await RecordAccess.RedactRowAsync(_db, table, fields, Visitor, row);

        Assert.False(shown.ContainsKey("salary"));
    }

    [Fact]
    public async Task WriteRuleRefusesOnlyWhenTheFieldIsSent()
    {
        var (table, fields) = await PeopleAsync(salaryWrite: "_USER_.role = 'consumer'");

        Assert.NotNull(await RecordAccess.WriteRefusalAsync(_db, table, fields, Visitor, new JsonObject { ["salary"] = 1 }));
        Assert.Null(await RecordAccess.WriteRefusalAsync(_db, table, fields, Visitor, new JsonObject { ["name"] = "x" }));
        Assert.Null(await RecordAccess.WriteRefusalAsync(_db, table, fields, Staff, new JsonObject { ["salary"] = 1 }));
    }

    [Fact]
    public async Task ReplaceCountsAsWritingEveryGuardedField()
    {
        var (table, fields) = await PeopleAsync(salaryWrite: "_USER_.role = 'consumer'");
        var record = await PersonAsync(table, "Ann", "visitor", 5000);

        Assert.NotNull(await RecordAccess.WriteRefusalAsync(_db, table, fields, Visitor, new JsonObject { ["name"] = "x" }, record.Id, replace: true));
    }

    [Fact]
    public async Task WriteRuleSeesRowAndRequest()
    {
        var (table, fields) = await PeopleAsync(salaryWrite: "_ROW_.owner = _USER_.id AND _REQ_.salary < 10000");
        var record = await PersonAsync(table, "Ann", "visitor", 5000);

        Assert.Null(await RecordAccess.WriteRefusalAsync(_db, table, fields, Visitor, new JsonObject { ["salary"] = 9000 }, record.Id));
        Assert.NotNull(await RecordAccess.WriteRefusalAsync(_db, table, fields, Visitor, new JsonObject { ["salary"] = 20000 }, record.Id));
        Assert.NotNull(await RecordAccess.WriteRefusalAsync(_db, table, fields, Staff, new JsonObject { ["salary"] = 9000 }, record.Id));
    }

    [Fact]
    public async Task TransactionHonoursWriteRule()
    {
        var (table, _) = await PeopleAsync(salaryWrite: "_USER_.role = 'consumer'");
        var record = await PersonAsync(table, "Ann", "visitor", 5000);
        var visitor = new UserAccount { Id = "visitor", Role = AccountRoles.User };

        var outcome = await RecordTransactions.ExecuteAsync(_db,
            [new("update", "people", record.Id, new JsonObject { ["salary"] = 1 })], transactional: true, visitor, Ct);

        Assert.Equal(ApiProblem.Forbidden, outcome.Problem);
        Assert.Contains("5000", (await _db.Records.AsNoTracking().SingleAsync(r => r.Id == record.Id, Ct)).JsonData);
    }

    [Fact]
    public async Task GuardedFieldIsNotSearchable()
    {
        var (table, fields) = await PeopleAsync(salaryRead: "_USER_.role = 'consumer'");
        await PersonAsync(table, "Ann", "staff", 5432);
        await RecordIndexes.SyncAsync(_db, table);

        var hidden = await QueryEngine.ListAsync(_db, table, [], null, false, "5432", 1, 25, accessFields: fields, access: Visitor);
        var named = await QueryEngine.ListAsync(_db, table, [], null, false, "Ann", 1, 25, accessFields: fields, access: Visitor);

        Assert.Equal(0, hidden.Total);
        Assert.Equal(1, named.Total);
    }

    [Fact]
    public async Task WireViewMasksGuardedColumn()
    {
        var (table, _) = await PeopleAsync(salaryRead: "_USER_.role = 'consumer'");
        await PersonAsync(table, "Ann", "staff", 5000);

        var result = await SqlEngine.ReadAsync(_db, "SELECT name, salary FROM \"People\" WHERE salary > 1",
            conn => WireCatalog.Apply(conn, WireDialect.Postgres, new UserAccount { Id = "visitor", Role = AccountRoles.User }));

        Assert.Null(result.Error);
        Assert.Empty(result.Rows);
    }

    [Theory]
    [InlineData("_USER_.role = 'consumer'", "", true)]
    [InlineData("_REQ_.salary > 1", "", false)]
    [InlineData("", "_REQ_.salary > 1", true)]
    [InlineData("_ROW_.nosuch = 1", "", false)]
    [InlineData("", "1; DROP TABLE x", false)]
    public async Task FieldRulesAreValidated(string read, string write, bool valid)
    {
        var (table, fields) = await PeopleAsync();
        var salary = fields.Single(f => f.Name == "salary");
        salary.ReadRule = read;
        salary.WriteRule = write;

        Assert.Equal(valid, await RecordAccess.FieldRuleProblemAsync(_db, table, fields, salary) is null);
    }
}
