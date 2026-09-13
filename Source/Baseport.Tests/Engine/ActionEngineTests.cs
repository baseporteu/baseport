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

    [Fact]
    public void An_http_request_step_requires_a_url()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"httpRequest"}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("URL is required"));
    }

    [Fact]
    public void An_http_request_step_refuses_a_private_target()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"httpRequest","url":"http://169.254.169.254/latest"}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_http_request_step_rejects_an_unknown_method()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"httpRequest","url":"https://example.com/hook","method":"TRACE"}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("method must be one of"));
    }

    [Fact]
    public void An_http_request_step_validates_each_bodyTemplate_expression()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"httpRequest","url":"https://example.com/hook","bodyTemplate":{"units":"data.Ghost"}}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("Ghost"));
    }

    [Fact]
    public void A_valid_http_request_step_is_accepted()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"httpRequest","url":"https://example.com/hook","method":"post","headers":{"X-Key":"abc"},"bodyTemplate":{"units":"Qty * 2"}}]"""), Fields);
        Assert.Empty(errs);
    }
}

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
        ProxyTarget.Configure(new AppSettings());
    }

    public void Dispose()
    {
        ActionDefCache.Reload(Array.Empty<ActionDef>());
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

    private static readonly IHttpClientFactory NoHttp = new ThrowingHttpClientFactory();
    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("This test does not expect an HTTP call.");
    }

    private sealed class HttpStub : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public HttpStub(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _reply(request);
        }
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
        await _db.SaveChangesAsync();

        var runs = await _db.PendingActionRuns.Where(r => r.ActionDefId == action.Id).ToListAsync();
        var run = Assert.Single(runs);
        Assert.Equal(ActionRunStatus.Pending, run.Status);
        Assert.Equal(record.Id, run.RecordId);

        await ActionRunner.RunAsync(_db, run, NoHttp, _log, default);
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
        await ActionRunner.RunAsync(_db, createRun, NoHttp, _log, default);
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

        var existing = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = """{"Code":"dup"}""", CreatedAt = DateTime.UtcNow };
        _db.Records.Add(existing);
        await _db.SaveChangesAsync();

        var obj = (JsonObject)JsonNode.Parse("""{ "Code": "fresh" }""")!;
        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync();

        var run = Assert.Single(await _db.PendingActionRuns.Where(r => r.RecordId == record.Id).ToListAsync());
        await ActionRunner.RunAsync(_db, run, NoHttp, _log, default);
        await _db.SaveChangesAsync();

        Assert.Equal(ActionRunStatus.Pending, run.Status);
        Assert.Equal(1, run.Attempts);
        Assert.True(run.NextAttemptAt > DateTime.UtcNow);
        Assert.NotEmpty(run.LastError);
    }

    [Fact]
    public async Task An_http_request_step_posts_the_bodyTemplate_evaluated_against_the_record()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Status", DataType = "text" });

        var action = new ActionDef
        {
            Id = Ids.NewShortId(12), TableId = table.Id, Name = "Notify", TriggerKind = ActionTriggers.OnCreate,
            StepsJson = """[{"type":"httpRequest","url":"https://example.com/hook","bodyTemplate":{"units":"Qty * 2"}}]"""
        };
        _db.Actions.Add(action);
        await _db.SaveChangesAsync();
        ActionDefCache.Reload(new[] { action });

        var obj = (JsonObject)JsonNode.Parse("""{ "Qty": 3 }""")!;
        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync();
        var run = Assert.Single(await _db.PendingActionRuns.Where(r => r.ActionDefId == action.Id).ToListAsync());

        var http = new HttpStub(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        await ActionRunner.RunAsync(_db, run, http, _log, default);
        await _db.SaveChangesAsync();

        Assert.Equal(ActionRunStatus.Done, run.Status);
        var sent = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://example.com/hook", sent.RequestUri!.ToString());
        Assert.Equal("""{"units":6}""", http.Bodies[0]);
    }

    [Fact]
    public async Task An_http_request_step_retries_on_a_failure_status_the_same_way_updateRecord_does()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" });
        var action = new ActionDef
        {
            Id = Ids.NewShortId(12), TableId = table.Id, Name = "Notify", TriggerKind = ActionTriggers.OnCreate,
            StepsJson = """[{"type":"httpRequest","url":"https://example.com/hook"}]"""
        };
        _db.Actions.Add(action);
        await _db.SaveChangesAsync();
        ActionDefCache.Reload(new[] { action });

        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = """{"Qty":1}""", CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync();
        var run = Assert.Single(await _db.PendingActionRuns.Where(r => r.ActionDefId == action.Id).ToListAsync());

        var http = new HttpStub(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        await ActionRunner.RunAsync(_db, run, http, _log, default);
        await _db.SaveChangesAsync();

        Assert.Equal(ActionRunStatus.Pending, run.Status);
        Assert.Equal(1, run.Attempts);
        Assert.Contains("503", run.LastError);
    }

    [Fact]
    public async Task An_http_request_step_refuses_a_private_target_even_if_it_passed_validation_at_save_time()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" });
        var action = new ActionDef
        {
            Id = Ids.NewShortId(12), TableId = table.Id, Name = "Notify", TriggerKind = ActionTriggers.OnCreate,

            StepsJson = """[{"type":"httpRequest","url":"http://169.254.169.254/latest"}]"""
        };
        _db.Actions.Add(action);
        await _db.SaveChangesAsync();
        ActionDefCache.Reload(new[] { action });

        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = """{"Qty":1}""", CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync();
        var run = Assert.Single(await _db.PendingActionRuns.Where(r => r.ActionDefId == action.Id).ToListAsync());

        await ActionRunner.RunAsync(_db, run, NoHttp, _log, default);
        await _db.SaveChangesAsync();

        Assert.Equal(ActionRunStatus.Pending, run.Status);
        Assert.Contains("private", run.LastError, StringComparison.OrdinalIgnoreCase);
    }
}
