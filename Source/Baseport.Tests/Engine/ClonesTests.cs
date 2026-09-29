using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Baseport;

namespace Baseport.Tests;

[Collection(nameof(RecordEvents))]
public class ClonesTests : IDisposable
{
    private const string Origin = "https://93.184.216.34";
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private TableDefinition _table = null!;
    private Connection _remote = null!;

    public ClonesTests()
    {
        TestSecrets.Ensure();
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        SchemaBootstrap.ApplyAsync(_db).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer(request));
    }

    private static HttpResponseMessage Json(JsonNode body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static int Page(HttpRequestMessage r) =>
        QueryHelpers.ParseQuery(r.RequestUri!.Query).TryGetValue("page", out var v) && int.TryParse(v.ToString(), out var n) ? n : 1;

    private static Func<HttpRequestMessage, HttpResponseMessage> Serve(IReadOnlyList<JsonObject> rows) => r =>
    {
        var page = Page(r);
        var slice = rows.Skip((page - 1) * 100).Take(100).Select(o => (JsonNode)o.DeepClone()).ToArray();
        return Json(new JsonObject { ["items"] = new JsonArray(slice), ["page"] = page, ["totalPages"] = Math.Max(1, (rows.Count + 99) / 100) });
    };

    private static List<JsonObject> Rows(IEnumerable<int> ids, Func<int, int>? qty = null) =>
        ids.Select(i => new JsonObject { ["sku"] = $"S{i}", ["qty"] = qty?.Invoke(i) ?? i }).ToList();

    private async Task<Clone> SetupAsync(string mode, bool allowLargeDeletes = false)
    {
        _remote = new Connection { Id = Ids.NewShortId(12), Name = "erp", BaseUrl = Origin + "/api" };
        _table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Stock", CreatedAt = DateTime.UtcNow };
        _table.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = _table.Id, Name = "sku", DataType = "text", IsUnique = true, Position = 0 });
        _table.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = _table.Id, Name = "qty", DataType = "number", Position = 1 });
        _table.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = _table.Id, Name = "note", DataType = "text", Position = 2 });
        var clone = new Clone
        {
            Id = Ids.NewShortId(12),
            Name = "stock",
            ConnectionId = _remote.Id,
            TableId = _table.Id,
            Path = "stock",
            Mode = mode,
            KeyField = mode == CloneModes.Append ? "" : "sku",
            AllowLargeDeletes = allowLargeDeletes,
            CreatedAt = DateTime.UtcNow
        };
        _db.Connections.Add(_remote);
        _db.Tables.Add(_table);
        _db.Clones.Add(clone);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RecordIndexes.SyncAsync(_db, _table);
        return clone;
    }

    private async Task<ImportRun> RunAsync(Clone clone, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var run = await Clones.QueueAsync(_db, clone, DateTime.UtcNow, TestContext.Current.CancellationToken);
        Assert.NotNull(run);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient(new Stub(answer));
        await ImportRuns.ExecuteAsync(_db, http, run!.Id, TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
        return await _db.ImportRuns.SingleAsync(r => r.Id == run.Id, TestContext.Current.CancellationToken);
    }

    private async Task<Dictionary<string, JsonObject>> LocalAsync()
    {
        var records = await _db.Records.AsNoTracking().Where(r => r.TableId == _table.Id).ToListAsync(TestContext.Current.CancellationToken);
        return records.Select(r => (JsonNode.Parse(r.JsonData) as JsonObject)!).ToDictionary(o => o["sku"]!.GetValue<string>());
    }

    [Fact]
    public async Task UpsertInsertsThenUpdatesOnlyWhatChanged()
    {
        var clone = await SetupAsync(CloneModes.Upsert);

        var first = await RunAsync(clone, Serve(Rows(Enumerable.Range(0, 250))));
        Assert.Equal((250, 0, ImportRunStatus.Done), (first.Inserted, first.Updated, first.Status));

        var second = await RunAsync(clone, Serve(Rows(Enumerable.Range(0, 270), i => i < 10 ? i + 1000 : i)));

        Assert.Equal((20, 10, 0), (second.Inserted, second.Updated, second.Deleted));
        var local = await LocalAsync();
        Assert.Equal(270, local.Count);
        Assert.Equal(1003, local["S3"]["qty"]!.GetValue<double>());
    }

    [Fact]
    public async Task UpsertKeepsLocalOnlyFields()
    {
        var clone = await SetupAsync(CloneModes.Upsert);
        await RunAsync(clone, Serve(Rows([1])));
        var record = await _db.Records.SingleAsync(TestContext.Current.CancellationToken);
        record.JsonData = """{"sku":"S1","qty":1,"note":"local"}""";
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RunAsync(clone, Serve(Rows([1], _ => 5)));

        var local = await LocalAsync();
        Assert.Equal("local", local["S1"]["note"]!.GetValue<string>());
        Assert.Equal(5, local["S1"]["qty"]!.GetValue<double>());
    }

    [Fact]
    public async Task RowsWithoutKeyAreRejected()
    {
        var clone = await SetupAsync(CloneModes.Upsert);

        var run = await RunAsync(clone, Serve([new JsonObject { ["qty"] = 1 }, new JsonObject { ["sku"] = "S1", ["qty"] = 1 }]));

        Assert.Equal((1, 1), (run.Inserted, run.Rejected));
        Assert.Contains("key field", ImportRuns.Dto(run).Errors.Single());
    }

    [Fact]
    public async Task MirrorMatchesTheRemoteExactly()
    {
        var clone = await SetupAsync(CloneModes.Mirror);
        await RunAsync(clone, Serve(Rows(Enumerable.Range(0, 100))));

        var run = await RunAsync(clone, Serve(Rows(Enumerable.Range(20, 85), i => i == 50 ? -1 : i)));

        Assert.Equal((ImportRunStatus.Done, 5, 1, 20), (run.Status, run.Inserted, run.Updated, run.Deleted));
        var local = await LocalAsync();
        Assert.Equal(Enumerable.Range(20, 85).Select(i => $"S{i}").Order(), local.Keys.Order());
        Assert.Equal(-1, local["S50"]["qty"]!.GetValue<double>());
    }

    [Fact]
    public async Task MirrorRefusesALargeDelete()
    {
        var clone = await SetupAsync(CloneModes.Mirror);
        await RunAsync(clone, Serve(Rows(Enumerable.Range(0, 100))));

        var run = await RunAsync(clone, Serve(Rows(Enumerable.Range(0, 10), _ => 999)));

        Assert.Equal(ImportRunStatus.Failed, run.Status);
        Assert.Contains("90 of 100", run.Message);
        var local = await LocalAsync();
        Assert.Equal(100, local.Count);
        Assert.Equal(0, local["S0"]["qty"]!.GetValue<double>());
    }

    [Fact]
    public async Task MirrorAllowsALargeDeleteWhenAsked()
    {
        var clone = await SetupAsync(CloneModes.Mirror, allowLargeDeletes: true);
        await RunAsync(clone, Serve(Rows(Enumerable.Range(0, 100))));

        var run = await RunAsync(clone, Serve(Rows(Enumerable.Range(0, 10))));

        Assert.Equal((ImportRunStatus.Done, 90), (run.Status, run.Deleted));
        Assert.Equal(10, (await LocalAsync()).Count);
    }

    [Fact]
    public async Task MirrorChangesNothingWhenTheFetchFails()
    {
        var clone = await SetupAsync(CloneModes.Mirror);
        await RunAsync(clone, Serve(Rows(Enumerable.Range(0, 250))));
        var rows = Rows(Enumerable.Range(0, 250), _ => 9999);
        var serve = Serve(rows);

        var run = await RunAsync(clone, r => Page(r) == 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : serve(r));

        Assert.Equal(ImportRunStatus.Failed, run.Status);
        var local = await LocalAsync();
        Assert.Equal(250, local.Count);
        Assert.All(local.Values, o => Assert.NotEqual(9999, o["qty"]!.GetValue<double>()));
    }

    [Fact]
    public async Task MirrorChangesNothingOnAnIncompleteFetch()
    {
        var clone = await SetupAsync(CloneModes.Mirror);
        await RunAsync(clone, Serve(Rows(Enumerable.Range(0, 50))));

        var run = await RunAsync(clone, _ => Json(new JsonObject { ["items"] = new JsonArray(Rows([1]).Select(o => (JsonNode)o).ToArray()), ["next"] = "/api/stock" }));

        Assert.Equal(ImportRunStatus.Failed, run.Status);
        Assert.Contains("incomplete", run.Message);
        Assert.Equal(50, (await LocalAsync()).Count);
    }

    [Fact]
    public async Task OneRunAtATime()
    {
        var clone = await SetupAsync(CloneModes.Upsert);
        Assert.NotNull(await Clones.QueueAsync(_db, clone, DateTime.UtcNow, TestContext.Current.CancellationToken));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await Clones.QueueAsync(_db, clone, DateTime.UtcNow, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DueClonesAreQueuedAndRescheduled()
    {
        var clone = await SetupAsync(CloneModes.Upsert);
        var now = DateTime.UtcNow;
        clone.Schedule = "0 */5 * * * *";
        clone.NextRunAt = now.AddMinutes(-1);
        var disabled = new Clone { Id = Ids.NewShortId(12), Name = "off", ConnectionId = _remote.Id, TableId = _table.Id, Mode = CloneModes.Append, Enabled = false, NextRunAt = now.AddMinutes(-1) };
        _db.Clones.Add(disabled);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var queued = await Clones.QueueDueAsync(_db, now, TestContext.Current.CancellationToken);

        Assert.Single(queued);
        _db.ChangeTracker.Clear();
        var after = await _db.Clones.SingleAsync(c => c.Id == clone.Id, TestContext.Current.CancellationToken);
        Assert.True(after.NextRunAt > now);
        Assert.Equal(queued[0], after.LastRunId);
    }

    [Theory]
    [InlineData("upsert", "", "key field")]
    [InlineData("mirror", "nope", "key field")]
    [InlineData("sideways", "sku", "Mode")]
    public async Task RulesRefuseBadConfig(string mode, string key, string expected)
    {
        await SetupAsync(CloneModes.Upsert);
        var clone = new Clone { Id = "x", Name = "bad", ConnectionId = _remote.Id, TableId = _table.Id, Mode = mode, KeyField = key };

        Assert.Contains(await Clones.ProblemsAsync(_db, clone, TestContext.Current.CancellationToken), e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RulesRefuseBadScheduleAndProxy()
    {
        await SetupAsync(CloneModes.Upsert);
        var proxy = new TableDefinition { Id = Ids.NewShortId(12), Name = "Remote", IsProxy = true, CreatedAt = DateTime.UtcNow };
        _db.Tables.Add(proxy);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var bad = new Clone { Id = "x", Name = "bad", ConnectionId = _remote.Id, TableId = proxy.Id, Mode = CloneModes.Append, Schedule = "whenever" };
        var errors = await Clones.ProblemsAsync(_db, bad, TestContext.Current.CancellationToken);

        Assert.Contains(errors, e => e.Contains("cron", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("proxy", StringComparison.Ordinal));
    }
}
