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
    public void NameRequired()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"runExpression","expr":"1"}]""", name: " "), Fields);
        Assert.Contains(errs, e => e.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownTriggerRejected()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"runExpression","expr":"1"}]""", trigger: "onFrobnicate"), Fields);
        Assert.Contains(errs, e => e.Contains("Unknown trigger"));
    }

    [Fact]
    public void StepRequired()
    {
        var errs = FieldValidation.ValidateActionDef(Action("[]"), Fields);
        Assert.Contains(errs, e => e.Contains("at least one step"));
    }

    [Fact]
    public void UnknownStepRejected()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"launchMissiles"}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("unknown step type"));
    }

    [Fact]
    public void RunExpressionValidated()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"runExpression","expr":"data.Ghost + 1"}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("Ghost"));
    }

    [Fact]
    public void UpdateStepRejectsComputed()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"updateRecord","setJson":{"Total":"2"}}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("server-computed"));
    }

    [Fact]
    public void UpdateStepAccepted()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"updateRecord","setJson":{"Status":"'received'"}}]"""), Fields);
        Assert.Empty(errs);
    }

    [Fact]
    public void HttpStepNeedsUrl()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"httpRequest"}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("URL is required"));
    }

    [Fact]
    public void HttpStepRefusesPrivateTarget()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"httpRequest","url":"http://169.254.169.254/latest"}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("private", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HttpStepRejectsUnknownMethod()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"httpRequest","url":"https://example.com/hook","method":"TRACE"}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("method must be one of"));
    }

    [Fact]
    public void HttpStepValidatesTemplate()
    {
        var errs = FieldValidation.ValidateActionDef(Action("""[{"type":"httpRequest","url":"https://example.com/hook","bodyTemplate":{"units":"data.Ghost"}}]"""), Fields);
        Assert.Contains(errs, e => e.Contains("Ghost"));
    }

    [Fact]
    public void HttpStepAccepted()
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
    public async Task CreateEnqueuesRun()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Status", DataType = "text" });

        var action = new ActionDef
        {
            Id = Ids.NewShortId(12),
            TableId = table.Id,
            Name = "Mark received",
            TriggerKind = ActionTriggers.OnCreate,
            StepsJson = """[{"type":"updateRecord","setJson":{"Status":"'received'"}}]"""
        };
        _db.Actions.Add(action);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        ActionDefCache.Reload(new[] { action });

        var obj = (JsonObject)JsonNode.Parse("""{ "Qty": 3 }""")!;
        var outcome = await RecordEngine.PrepareAsync(_db, table, table.Fields, obj);
        Assert.Empty(outcome.Errors);
        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var runs = await _db.PendingActionRuns.Where(r => r.ActionDefId == action.Id).ToListAsync(TestContext.Current.CancellationToken);
        var run = Assert.Single(runs);
        Assert.Equal(ActionRunStatus.Pending, run.Status);
        Assert.Equal(record.Id, run.RecordId);

        await ActionRunner.RunAsync(_db, run, NoHttp, _log, TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ActionRunStatus.Done, run.Status);
        var stored = JsonNode.Parse(record.JsonData)!;
        Assert.Equal("received", stored["Status"]!.GetValue<string>());
    }

    [Fact]
    public async Task SelfUpdateDoesNotRequeue()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Status", DataType = "text" });

        var onCreate = new ActionDef
        {
            Id = Ids.NewShortId(12),
            TableId = table.Id,
            Name = "Mark received",
            TriggerKind = ActionTriggers.OnCreate,
            StepsJson = """[{"type":"updateRecord","setJson":{"Status":"'received'"}}]"""
        };

        var onUpdate = new ActionDef
        {
            Id = Ids.NewShortId(12),
            TableId = table.Id,
            Name = "Log update",
            TriggerKind = ActionTriggers.OnUpdate,
            StepsJson = """[{"type":"runExpression","expr":"1"}]"""
        };
        _db.Actions.AddRange(onCreate, onUpdate);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        ActionDefCache.Reload(new[] { onCreate, onUpdate });

        var obj = (JsonObject)JsonNode.Parse("""{ "Qty": 1 }""")!;
        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var createRun = Assert.Single(await _db.PendingActionRuns.Where(r => r.ActionDefId == onCreate.Id).ToListAsync(TestContext.Current.CancellationToken));
        await ActionRunner.RunAsync(_db, createRun, NoHttp, _log, TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var updateRuns = await _db.PendingActionRuns.Where(r => r.ActionDefId == onUpdate.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Empty(updateRuns);
    }

    [Fact]
    public async Task FailingStepRetries()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Code", DataType = "text", IsUnique = true });

        var action = new ActionDef
        {
            Id = Ids.NewShortId(12),
            TableId = table.Id,
            Name = "Collide",
            TriggerKind = ActionTriggers.OnCreate,
            StepsJson = """[{"type":"updateRecord","setJson":{"Code":"'dup'"}}]"""
        };
        _db.Actions.Add(action);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        ActionDefCache.Reload(new[] { action });

        var existing = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = """{"Code":"dup"}""", CreatedAt = DateTime.UtcNow };
        _db.Records.Add(existing);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var obj = (JsonObject)JsonNode.Parse("""{ "Code": "fresh" }""")!;
        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var run = Assert.Single(await _db.PendingActionRuns.Where(r => r.RecordId == record.Id).ToListAsync(TestContext.Current.CancellationToken));
        await ActionRunner.RunAsync(_db, run, NoHttp, _log, TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ActionRunStatus.Pending, run.Status);
        Assert.Equal(1, run.Attempts);
        Assert.True(run.NextAttemptAt > DateTime.UtcNow);
        Assert.NotEmpty(run.LastError);
    }

    [Fact]
    public async Task HttpStepPostsTemplate()
    {
        var table = Seed(
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" },
            new FieldDefinition { Id = Ids.NewShortId(12), Name = "Status", DataType = "text" });

        var action = new ActionDef
        {
            Id = Ids.NewShortId(12),
            TableId = table.Id,
            Name = "Notify",
            TriggerKind = ActionTriggers.OnCreate,
            StepsJson = """[{"type":"httpRequest","url":"https://example.com/hook","bodyTemplate":{"units":"Qty * 2"}}]"""
        };
        _db.Actions.Add(action);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        ActionDefCache.Reload(new[] { action });

        var obj = (JsonObject)JsonNode.Parse("""{ "Qty": 3 }""")!;
        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = obj.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var run = Assert.Single(await _db.PendingActionRuns.Where(r => r.ActionDefId == action.Id).ToListAsync(TestContext.Current.CancellationToken));

        var http = new HttpStub(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        await ActionRunner.RunAsync(_db, run, http, _log, TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ActionRunStatus.Done, run.Status);
        var sent = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://example.com/hook", sent.RequestUri!.ToString());
        Assert.Equal("""{"units":6}""", http.Bodies[0]);
    }

    [Fact]
    public async Task HttpStepRetriesOnFailure()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" });
        var action = new ActionDef
        {
            Id = Ids.NewShortId(12),
            TableId = table.Id,
            Name = "Notify",
            TriggerKind = ActionTriggers.OnCreate,
            StepsJson = """[{"type":"httpRequest","url":"https://example.com/hook"}]"""
        };
        _db.Actions.Add(action);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        ActionDefCache.Reload(new[] { action });

        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = """{"Qty":1}""", CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var run = Assert.Single(await _db.PendingActionRuns.Where(r => r.ActionDefId == action.Id).ToListAsync(TestContext.Current.CancellationToken));

        var http = new HttpStub(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        await ActionRunner.RunAsync(_db, run, http, _log, TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ActionRunStatus.Pending, run.Status);
        Assert.Equal(1, run.Attempts);
        Assert.Contains("503", run.LastError);
    }

    [Fact]
    public async Task HttpStepRechecksTarget()
    {
        var table = Seed(new FieldDefinition { Id = Ids.NewShortId(12), Name = "Qty", DataType = "number" });
        var action = new ActionDef
        {
            Id = Ids.NewShortId(12),
            TableId = table.Id,
            Name = "Notify",
            TriggerKind = ActionTriggers.OnCreate,

            StepsJson = """[{"type":"httpRequest","url":"http://169.254.169.254/latest"}]"""
        };
        _db.Actions.Add(action);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        ActionDefCache.Reload(new[] { action });

        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = """{"Qty":1}""", CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var run = Assert.Single(await _db.PendingActionRuns.Where(r => r.ActionDefId == action.Id).ToListAsync(TestContext.Current.CancellationToken));

        await ActionRunner.RunAsync(_db, run, NoHttp, _log, TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ActionRunStatus.Pending, run.Status);
        Assert.Contains("private", run.LastError, StringComparison.OrdinalIgnoreCase);
    }
}
