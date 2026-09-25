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
    public async Task A_rolled_back_transactional_batch_emits_no_event()
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
    public async Task A_committed_transactional_batch_emits_every_event_once()
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
    public async Task A_non_transactional_write_still_emits_immediately()
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
    public async Task Sequential_operations_apply_and_return_one_id_per_operation()
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
    public async Task Non_transactional_mode_keeps_earlier_operations_when_a_later_one_fails()
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
    public async Task Transactional_mode_rolls_back_every_operation_when_one_fails()
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
    public async Task A_later_operation_in_the_same_batch_sees_an_earlier_ones_write()
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
    public async Task Batch_size_over_the_limit_is_refused_before_touching_the_database()
    {
        await NotesAsync();
        var ops = Enumerable.Range(0, 129).Select(i => Create("notes", i.ToString())).ToList();

        var outcome = await RecordTransactions.ExecuteAsync(_db, ops, transactional: false, Caller(), TestContext.Current.CancellationToken);

        Assert.True(outcome.HasErrors);
        Assert.Equal(ApiProblem.BadRequest, outcome.Problem);
        Assert.Equal(0, await _db.Records.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_access_rule_that_refuses_the_operation_aborts_the_batch()
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
    public async Task A_read_only_key_cannot_delete_even_when_the_table_allows_it()
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
    public async Task A_verb_the_table_does_not_publish_refuses_the_operation()
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
