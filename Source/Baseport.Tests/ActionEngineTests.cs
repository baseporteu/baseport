using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Baseport.Tests;

public class ActionDefValidationTests
{
    private static readonly List<FieldDefinition> Fields = new()
    {
        new() { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" },
        new() { Id = Ids.NewShortId(12), Name = "Status", DataType = "text" },
        new() { Id = Ids.NewShortId(12), Name = "Total", DataType = "calculated", Expression = "1" }
    };

    private static ActionDef Action(string stepsJson, string trigger = ActionTriggers.OnCreate, string name = "My action") =>
        new() { Name = name, TriggerKind = trigger, StepsJson = stepsJson };

    [Fact]
    public void Name_is_required()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"runExpression","expr":"1"}]""", name: " "), Fields);
        Assert.Contains(errs, e => e.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unknown_trigger_is_rejected()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"runExpression","expr":"1"}]""", trigger: "onFrobnicate"), Fields);
        Assert.Contains(errs, e => e.Contains("Unknown trigger"));
    }

    [Fact]
    public void At_least_one_step_is_required()
    {
        var errs = FieldValidation.ValidateActionDef(Action("[]"), Fields);
        Assert.Contains(errs, e => e.Contains("at least one step"));
    }

    [Fact]
    public void An_unknown_step_type_is_rejected()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"launchMissiles"}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("unknown step type"));
    }

    [Fact]
    public void A_run_expression_step_validates_its_expression()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"runExpression","expr":"data.Ghost + 1"}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("Ghost"));
    }

    [Fact]
    public void An_update_record_step_rejects_a_computed_field()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"updateRecord","setJson":{"Total":"2"}}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("server-computed"));
    }

    [Fact]
    public void A_valid_update_record_step_is_accepted()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"updateRecord","setJson":{"Status":"'received'"}}]"""), Fields);
        Assert.Empty(errs);
    }
}

// Exercises the full durable path: RecordChangeInterceptor enqueues on a real write, JobScheduler's own
// ActionRunner.RunAsync applies it, and a self-triggering action does not create a second run.
public class ActionEngineIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Serilog.ILogger _log = Serilog.Log.Logger;

    public ActionEngineIntegrationTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        ActionDefCache.Reload(Array.Empty<ActionDef>()); // this process's cache outlives the test's own in-memory db
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private TableDefinition Seed(params FieldDefinition[] fields)
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders", Fields = fields.ToList() };
        _db.Tables.Add(table);
        _db.SaveChanges();
        RecordIndexes.SyncAsync(_db, table).GetAwaiter().GetResult();
        return table;
    }

    [Fact]
    public async Task Creating_a_record_enqueues_a_durable_run_that_the_runner_then_applies()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Status", DataType = "text" });

        var action = new ActionDef
        {
            Id = Ids.NewShortId(12), TableId = table.Id, Name = "Mark received", TriggerKind = ActionTriggers.OnCreate,
            StepsJson = """[{"type":"updateRecord","setJson":{"Status":"'received'"}}]"""
        };
        _db.Actions.Add(action);
        await _db.SaveChangesAsync();
        ActionDefCache.Reload(new[] { action });

        var obj = (JsonObject)JsonNode.Parse("""{ "Qty": 3 }""")!;
        var outcome = await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);
        Assert.Empty(outcome.Errors);
        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(); // interceptor fires here

        var runs = await _db.PendingActionRuns.Where(r => r.ActionDefId == action.Id).ToListAsync();
        var run = Assert.Single(runs);
        Assert.Equal(ActionRunStatus.Pending, run.Status);
        Assert.Equal(record.Id, run.RecordId);

        await ActionRunner.RunAsync(_db, run, _log, default);
        await _db.SaveChangesAsync();

        Assert.Equal(ActionRunStatus.Done, run.Status);
        var stored = JsonNode.Parse(record.JsonData)!;
        Assert.Equal("received", stored["Status"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_action_updating_its_own_record_does_not_enqueue_a_second_run_for_itself()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Status", DataType = "text" });

        var onCreate = new ActionDef
        {
            Id = Ids.NewShortId(12), TableId = table.Id, Name = "Mark received", TriggerKind = ActionTriggers.OnCreate,
            StepsJson = """[{"type":"updateRecord","setJson":{"Status":"'received'"}}]"""
        };
        // Watches the same table's updates - would fire a second time if the runner's own write were not suppressed.
        var onUpdate = new ActionDef
        {
            Id = Ids.NewShortId(12), TableId = table.Id, Name = "Log update", TriggerKind = ActionTriggers.OnUpdate,
            StepsJson = """[{"type":"runExpression","expr":"1"}]"""
        };
        _db.Actions.AddRange(onCreate, onUpdate);
        await _db.SaveChangesAsync();
        ActionDefCache.Reload(new[] { onCreate, onUpdate });

        var obj = (JsonObject)JsonNode.Parse("""{ "Qty": 1 }""")!;
        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync();

        var createRun = Assert.Single(await _db.PendingActionRuns.Where(r => r.ActionDefId == onCreate.Id).ToListAsync());
        await ActionRunner.RunAsync(_db, createRun, _log, default);
        await _db.SaveChangesAsync();

        var updateRuns = await _db.PendingActionRuns.Where(r => r.ActionDefId == onUpdate.Id).ToListAsync();
        Assert.Empty(updateRuns);
    }

    [Fact]
    public async Task A_failing_step_retries_with_backoff_instead_of_failing_immediately()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Code", DataType = "text", IsUnique = true });

        var action = new ActionDef
        {
            Id = Ids.NewShortId(12), TableId = table.Id, Name = "Collide", TriggerKind = ActionTriggers.OnCreate,
            StepsJson = """[{"type":"updateRecord","setJson":{"Code":"'dup'"}}]"""
        };
        _db.Actions.Add(action);
        await _db.SaveChangesAsync();
        ActionDefCache.Reload(new[] { action });

        // Already holds the value the action will collide into.
        var existing = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = """{"Code":"dup"}""", CreatedAt = DateTime.UtcNow };
        _db.Records.Add(existing);
        await _db.SaveChangesAsync();

        var obj = (JsonObject)JsonNode.Parse("""{ "Code": "fresh" }""")!;
        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync();

        var run = Assert.Single(await _db.PendingActionRuns.Where(r => r.RecordId == record.Id).ToListAsync());
        await ActionRunner.RunAsync(_db, run, _log, default);
        await _db.SaveChangesAsync();

        Assert.Equal(ActionRunStatus.Pending, run.Status);
        Assert.Equal(1, run.Attempts);
        Assert.True(run.NextAttemptAt > DateTime.UtcNow);
        Assert.NotEmpty(run.LastError);
    }
}
