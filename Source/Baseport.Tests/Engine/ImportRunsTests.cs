using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class ImportRunsTests : IDisposable
{
    private const string Origin = "https://93.184.216.34";
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public ImportRunsTests()
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

    private static HttpResponseMessage Paged(HttpRequestMessage r, int total, Func<int, JsonObject>? row = null)
    {
        var page = Page(r);
        var items = new JsonArray(Enumerable.Range((page - 1) * 100, Math.Max(0, Math.Min(100, total - (page - 1) * 100)))
            .Select(i => (JsonNode)(row?.Invoke(i) ?? new JsonObject { ["code"] = $"C{i}", ["qty"] = i })).ToArray());
        return Json(new JsonObject { ["items"] = items, ["page"] = page, ["totalPages"] = (total + 99) / 100 });
    }

    private async Task<(TableDefinition Table, ImportRun Run)> SetupAsync(bool requireQty = false, bool proxy = false)
    {
        var connection = new Connection { Id = Ids.NewShortId(12), Name = "api", BaseUrl = Origin + "/api" };
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Stock", IsProxy = proxy, CreatedAt = DateTime.UtcNow };
        table.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = table.Id, Name = "code", DataType = "text", Position = 0 });
        table.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = table.Id, Name = "qty", DataType = "number", IsRequired = requireQty, Min = 0, Position = 1 });
        var run = new ImportRun { Id = Ids.NewShortId(12), ConnectionId = connection.Id, TableId = table.Id, Path = "stock", CreatedAt = DateTime.UtcNow };
        _db.Connections.Add(connection);
        _db.Tables.Add(table);
        _db.ImportRuns.Add(run);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (table, run);
    }

    private async Task<ImportRun> ExecuteAsync(string runId, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        using var http = new HttpClient(new Stub(answer));
        await ImportRuns.ExecuteAsync(_db, http, runId, TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
        return await _db.ImportRuns.SingleAsync(r => r.Id == runId, TestContext.Current.CancellationToken);
    }

    private Task<int> RecordCount(string tableId) => _db.Records.CountAsync(r => r.TableId == tableId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task EveryPageLands()
    {
        var (table, run) = await SetupAsync();

        var done = await ExecuteAsync(run.Id, r => Paged(r, 250));

        Assert.Equal(ImportRunStatus.Done, done.Status);
        Assert.Equal((3, 250, 250, 0), (done.Pages, done.Rows, done.Inserted, done.Rejected));
        Assert.Equal("page", done.Strategy);
        Assert.Equal(250, await RecordCount(table.Id));
        Assert.NotNull(done.StartedAt);
        Assert.NotNull(done.FinishedAt);
    }

    [Fact]
    public async Task RejectedRowsUseGlobalNumbers()
    {
        var (table, run) = await SetupAsync();

        var done = await ExecuteAsync(run.Id, r => Paged(r, 250, i => new JsonObject { ["code"] = $"C{i}", ["qty"] = i == 150 ? -5 : i }));

        Assert.Equal(ImportRunStatus.Done, done.Status);
        Assert.Equal((249, 1), (done.Inserted, done.Rejected));
        Assert.Contains("Row 151", ImportRuns.Dto(done).Errors.Single());
        Assert.Equal(249, await RecordCount(table.Id));
    }

    [Fact]
    public async Task FailureMidRunKeepsEarlierPagesAndSaysWhy()
    {
        var (table, run) = await SetupAsync();

        var done = await ExecuteAsync(run.Id, r => Page(r) == 2 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : Paged(r, 250));

        Assert.Equal(ImportRunStatus.Failed, done.Status);
        Assert.Contains("502", done.Message);
        Assert.Equal(100, await RecordCount(table.Id));
    }

    [Fact]
    public async Task AllRejectedFails()
    {
        var (_, run) = await SetupAsync(requireQty: true);

        var done = await ExecuteAsync(run.Id, r => Paged(r, 30, i => new JsonObject { ["code"] = $"C{i}" }));

        Assert.Equal(ImportRunStatus.Failed, done.Status);
        Assert.Equal((0, 30), (done.Inserted, done.Rejected));
        Assert.True(ImportRuns.Dto(done).Errors.Count <= ImportRuns.MaxReportedErrors);
    }

    [Fact]
    public async Task ProxyTargetFails()
    {
        var (table, run) = await SetupAsync(proxy: true);

        var done = await ExecuteAsync(run.Id, r => Paged(r, 10));

        Assert.Equal(ImportRunStatus.Failed, done.Status);
        Assert.Equal(0, await RecordCount(table.Id));
    }

    [Fact]
    public async Task OnlyQueuedRunsExecute()
    {
        var (_, run) = await SetupAsync();
        run.Status = ImportRunStatus.Done;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var after = await ExecuteAsync(run.Id, r => Paged(r, 10));

        Assert.Equal(0, after.Inserted);
    }

    [Fact]
    public async Task InterruptedRunsFailOnStart()
    {
        var (_, run) = await SetupAsync();
        run.Status = ImportRunStatus.Running;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await ImportRuns.FailInterruptedAsync(_db, TestContext.Current.CancellationToken));
        _db.ChangeTracker.Clear();
        var failed = await _db.ImportRuns.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ImportRunStatus.Failed, failed.Status);
        Assert.Contains("restart", failed.Message);
    }
}
