using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Baseport.Tests;

[Collection(nameof(RecordEvents))]
public class RecordTransactionsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RecordTransactionsTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
    }

    private async Task<TableDefinition> NotesAsync(string createRule = "", string updateRule = "", string deleteRule = "")
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var table = new TableDefinition
        {
            Id = Ids.NewShortId(12),
            Name = "Notes",
            ApiName = "notes",
            ApiEnabled = true,
            ApiMethods = "GET,POST,PATCH,PUT,DELETE",
            CreateRule = createRule,
            UpdateRule = updateRule,
            DeleteRule = deleteRule
        };
        _db.Tables.Add(table);
        _db.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = table.Id, Name = "body", DataType = "text", Position = 0 });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return table;
    }

    private static RecordTransactions.Operation Create(string apiName, string body) =>
        new("create", apiName, null, new JsonObject { ["body"] = body });

    private static RecordTransactions.Operation Update(string apiName, string id, string body) =>
        new("update", apiName, id, new JsonObject { ["body"] = body });

    private static RecordTransactions.Operation Delete(string apiName, string id) =>
        new("delete", apiName, id, null);

    private static UserAccount Caller(string id = "tester", string role = AccountRoles.Consumer, string apiTokenMethods = ApiMethods.Default) =>
        new() { Id = id, Role = role, ApiTokenMethods = apiTokenMethods };

    private static List<RecordEvent> Drain(System.Threading.Channels.Channel<RecordEvent> channel, string tableId)
    {
        var events = new List<RecordEvent>();
        while (channel.Reader.TryRead(out var e))
            if (e.TableId == tableId) events.Add(e);
        return events;
    }

    [Fact]
    public async Task RollbackEmitsNoEvent()
    {
        var table = await NotesAsync();
        var channel = RecordEvents.TrySubscribe()!;
        try
        {
            var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
            {
                Create("notes", "never happened"),
                Update("notes", "no-such-record", "x")
            }, transactional: true, Caller(), TestContext.Current.CancellationToken);

            Assert.True(outcome.HasErrors);
            Assert.Empty(Drain(channel, table.Id));
        }
        finally
        {
            RecordEvents.Unsubscribe(channel);
        }
    }

    [Fact]
    public async Task CommitEmitsEachEventOnce()
    {
        var table = await NotesAsync();
        var channel = RecordEvents.TrySubscribe()!;
        try
        {
            var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
            {
                Create("notes", "one"),
                Create("notes", "two")
            }, transactional: true, Caller(), TestContext.Current.CancellationToken);

            Assert.False(outcome.HasErrors);
            Assert.Equal(outcome.Ids!.Order(), Drain(channel, table.Id).Select(e => e.RecordId).Order());
        }
        finally
        {
            RecordEvents.Unsubscribe(channel);
        }
    }

    [Fact]
    public async Task NonTransactionalEmitsAtOnce()
    {
        var table = await NotesAsync();
        var channel = RecordEvents.TrySubscribe()!;
        try
        {
            await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation> { Create("notes", "now") },
                transactional: false, Caller(), TestContext.Current.CancellationToken);

            Assert.Single(Drain(channel, table.Id));
        }
        finally
        {
            RecordEvents.Unsubscribe(channel);
        }
    }

    [Fact]
    public async Task OperationsReturnIds()
    {
        var table = await NotesAsync();

        var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
        {
            Create("notes", "first"),
            Create("notes", "second")
        }, transactional: false, Caller(), TestContext.Current.CancellationToken);

        Assert.False(outcome.HasErrors);
        Assert.Equal(2, outcome.Ids!.Count);
        Assert.Equal(2, await _db.Records.CountAsync(r => r.TableId == table.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonTransactionalKeepsEarlier()
    {
        var table = await NotesAsync();

        var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
        {
            Create("notes", "kept"),
            new("create", "no-such-table", null, new JsonObject())
        }, transactional: false, Caller(), TestContext.Current.CancellationToken);

        Assert.True(outcome.HasErrors);
        Assert.Equal(ApiProblem.NotFound, outcome.Problem);
        Assert.Equal(1, await _db.Records.CountAsync(r => r.TableId == table.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PartialFailureReportsCommittedIds()
    {
        await NotesAsync();

        var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
        {
            Create("notes", "one"),
            Create("notes", "two"),
            new("create", "no-such-table", null, new JsonObject()),
            Create("notes", "never")
        }, transactional: false, Caller(), TestContext.Current.CancellationToken);

        Assert.Equal(2, outcome.FailedIndex);
        Assert.Equal(2, outcome.Ids.Count);
        var stored = await _db.Records.Select(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(stored.Order(), outcome.Ids.Order());
    }

    [Fact]
    public async Task RolledBackBatchReportsNothingCompleted()
    {
        await NotesAsync();

        var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
        {
            Create("notes", "one"),
            new("create", "no-such-table", null, new JsonObject())
        }, transactional: true, Caller(), TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.FailedIndex);
        Assert.Empty(outcome.Ids);
        Assert.Equal(0, await _db.Records.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("update", "notes", null, "update needs a recordId.")]
    [InlineData("delete", "notes", "", "delete needs a recordId.")]
    [InlineData("upsert", "notes", "x", "'upsert' must be create, update or delete.")]
    [InlineData("create", "", null, "Each operation needs an apiName.")]
    public async Task MalformedBatchWritesNothing(string kind, string apiName, string? recordId, string detail)
    {
        await NotesAsync();

        var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
        {
            Create("notes", "one"),
            Create("notes", "two"),
            new(kind, apiName, recordId, new JsonObject())
        }, transactional: false, Caller(), TestContext.Current.CancellationToken);

        Assert.Equal(ApiProblem.BadRequest, outcome.Problem);
        Assert.Equal(detail, outcome.Detail);
        Assert.Equal(2, outcome.FailedIndex);
        Assert.Empty(outcome.Ids);
        Assert.Equal(0, await _db.Records.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void FailureExtensionsListCompletedPositions()
    {
        var outcome = new RecordTransactions.Outcome(["a", "b"], ApiProblem.NotFound, "gone", 2);

        var extensions = TransactionEndpoints.Extensions(outcome)!;

        Assert.Equal(2, extensions["index"]);
        Assert.Equal(new[] { new TransactionCompletedDto(0, "a"), new TransactionCompletedDto(1, "b") }, (IEnumerable<TransactionCompletedDto>)extensions["completed"]!);
        Assert.Null(TransactionEndpoints.Extensions(new RecordTransactions.Outcome([], ApiProblem.BadRequest, "x")));
    }

    [Fact]
    public void RequestRefusesUnknownMembers()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<TransactionRequest>(
            """{"operations":[],"transactional":true}""", System.Text.Json.JsonSerializerOptions.Web));
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<TransactionRequest>(
            """{"operations":[{"op":"create","apiName":"notes","record_id":"x"}]}""", System.Text.Json.JsonSerializerOptions.Web));
    }

    [Fact]
    public void SdkOperationShapeBinds()
    {
        var sdk = System.Text.Json.JsonSerializer.Serialize(new
        {
            operations = new[] { Baseport.Client.RecordOperation.Update("notes", "r1", new { body = "x" }) },
            transaction = true
        }, System.Text.Json.JsonSerializerOptions.Web);

        var request = System.Text.Json.JsonSerializer.Deserialize<TransactionRequest>(sdk, System.Text.Json.JsonSerializerOptions.Web)!;

        Assert.True(request.Transaction);
        var op = Assert.Single(request.Operations!);
        Assert.Equal("update", op.Op);
        Assert.Equal("notes", op.ApiName);
        Assert.Equal("r1", op.RecordId);
        Assert.Equal("x", op.Value!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task TransactionalRollsBackAll()
    {
        var table = await NotesAsync();

        var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
        {
            Create("notes", "rolled-back"),
            new("create", "no-such-table", null, new JsonObject())
        }, transactional: true, Caller(), TestContext.Current.CancellationToken);

        Assert.True(outcome.HasErrors);
        Assert.Equal(0, await _db.Records.CountAsync(r => r.TableId == table.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LaterOperationSeesEarlier()
    {
        await NotesAsync();
        var ops = new List<RecordTransactions.Operation> { Create("notes", "v1") };

        var first = await RecordTransactions.ExecuteAsync(_db, ops, transactional: true, Caller(), TestContext.Current.CancellationToken);
        var id = first.Ids![0];

        var second = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
        {
            Update("notes", id, "v2"),
            Delete("notes", id)
        }, transactional: true, Caller(), TestContext.Current.CancellationToken);

        Assert.False(second.HasErrors);
        Assert.Equal(0, await _db.Records.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizedBatchRefused()
    {
        await NotesAsync();
        var ops = Enumerable.Range(0, 129).Select(i => Create("notes", i.ToString())).ToList();

        var outcome = await RecordTransactions.ExecuteAsync(_db, ops, transactional: false, Caller(), TestContext.Current.CancellationToken);

        Assert.True(outcome.HasErrors);
        Assert.Equal(ApiProblem.BadRequest, outcome.Problem);
        Assert.Equal(0, await _db.Records.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RuleRefusalAbortsBatch()
    {
        await NotesAsync(createRule: "0");

        var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
        {
            Create("notes", "denied")
        }, transactional: false, Caller("someone"), TestContext.Current.CancellationToken);

        Assert.True(outcome.HasErrors);
        Assert.Equal(ApiProblem.Forbidden, outcome.Problem);
        Assert.Equal(0, await _db.Records.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadOnlyKeyCannotDelete()
    {
        await NotesAsync();
        var readOnly = Caller(apiTokenMethods: "GET");

        var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
        {
            Create("notes", "should be refused")
        }, transactional: false, readOnly, TestContext.Current.CancellationToken);

        Assert.True(outcome.HasErrors);
        Assert.Equal(ApiProblem.MethodNotAllowed, outcome.Problem);
        Assert.Equal(0, await _db.Records.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnpublishedVerbRefused()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var table = new TableDefinition
        {
            Id = Ids.NewShortId(12),
            Name = "Notes",
            ApiName = "notes",
            ApiEnabled = true,
            ApiMethods = "GET,POST"
        };
        _db.Tables.Add(table);
        _db.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = table.Id, Name = "body", DataType = "text", Position = 0 });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var outcome = await RecordTransactions.ExecuteAsync(_db, new List<RecordTransactions.Operation>
        {
            Delete("notes", "whatever")
        }, transactional: false, Caller(), TestContext.Current.CancellationToken);

        Assert.True(outcome.HasErrors);
        Assert.Equal(ApiProblem.MethodNotAllowed, outcome.Problem);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
