using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// A scheduled query is the operator's own task. It runs unattended, so what is pinned is that every way it can go wrong is recorded rather than thrown, and that the tick after it still happens.
public class ScheduledQueriesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public ScheduledQueriesTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.Migrate();
        ProxyTarget.Configure(new AppSettings());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class Stub : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public List<string> Bodies { get; } = new();

        public Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _reply(request);
        }
    }

    private static Stub Answering(System.Net.HttpStatusCode status) => new(_ => new HttpResponseMessage(status));

    private SavedQuery Save(string sql, string schedule = "0 7 * * *", string webhook = "", bool enabled = true)
    {
        var now = DateTime.UtcNow;
        var query = new SavedQuery
        {
            Id = Ids.NewShortId(12),
            Name = "Report",
            Sql = sql,
            Schedule = schedule,
            ScheduleEnabled = enabled,
            WebhookUrl = webhook,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.SavedQueries.Add(query);
        _db.SaveChanges();
        return query;
    }

    [Fact]
    public async Task A_run_without_a_destination_records_what_it_read()
    {
        var query = Save("SELECT 1 AS n");
        var now = DateTime.UtcNow;

        await ScheduledQueries.RunAsync(_db, query, Answering(System.Net.HttpStatusCode.OK), now, TestContext.Current.CancellationToken);

        Assert.Equal("Read 1 row(s).", query.LastResult);
        Assert.Equal(now, query.LastExecutedAt);
        Assert.True(query.NextRunAt > now);
    }

    [Fact]
    public async Task A_run_with_a_destination_posts_the_grid()
    {
        var stub = Answering(System.Net.HttpStatusCode.Accepted);
        var query = Save("SELECT 7 AS n", webhook: "https://example.com/hook");

        await ScheduledQueries.RunAsync(_db, query, stub, DateTime.UtcNow, TestContext.Current.CancellationToken);

        Assert.Contains("Posted 1 row(s)", query.LastResult);
        Assert.Contains("202", query.LastResult);
        Assert.Contains("\"n\"", stub.Bodies.Single());
        Assert.Contains("\"7\"", stub.Bodies.Single());
    }

    // The tick that follows must still happen, so a refused destination is a message on the query and not an exception out of the loop.
    [Fact]
    public async Task A_destination_that_refuses_is_recorded_rather_than_thrown()
    {
        var query = Save("SELECT 1 AS n", webhook: "https://example.com/hook");

        await ScheduledQueries.RunAsync(_db, query, Answering(System.Net.HttpStatusCode.InternalServerError),
            DateTime.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal("Failed: the endpoint answered 500.", query.LastResult);
    }

    [Fact]
    public async Task A_query_that_stopped_being_readable_never_reaches_the_destination()
    {
        var stub = Answering(System.Net.HttpStatusCode.OK);
        var query = Save("DELETE FROM _records", webhook: "https://example.com/hook");

        await ScheduledQueries.RunAsync(_db, query, stub, DateTime.UtcNow, TestContext.Current.CancellationToken);

        Assert.StartsWith("Failed:", query.LastResult);
        Assert.Empty(stub.Bodies);
        Assert.Null(query.LastExecutedAt);
    }

    // A name that resolved to a public address when it was saved can resolve to a private one by the time the tick reaches it.
    [Fact]
    public async Task A_destination_on_the_servers_own_network_is_refused_at_run_time()
    {
        var stub = Answering(System.Net.HttpStatusCode.OK);
        var query = Save("SELECT 1 AS n", webhook: "http://127.0.0.1:9/hook");

        await ScheduledQueries.RunAsync(_db, query, stub, DateTime.UtcNow, TestContext.Current.CancellationToken);

        Assert.Contains("private or loopback", query.LastResult);
        Assert.Empty(stub.Bodies);
    }

    [Fact]
    public async Task Only_an_enabled_schedule_that_is_due_comes_back()
    {
        var now = DateTime.UtcNow;
        var due = Save("SELECT 1 AS n");
        var paused = Save("SELECT 1 AS n", enabled: false);
        var later = Save("SELECT 1 AS n");
        var manual = Save("SELECT 1 AS n", schedule: "");

        due.NextRunAt = now.AddMinutes(-1);
        paused.NextRunAt = now.AddMinutes(-1);
        later.NextRunAt = now.AddHours(1);
        manual.NextRunAt = now.AddMinutes(-1);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var found = await ScheduledQueries.DueAsync(_db, now, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { due.Id }, found.Select(q => q.Id));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("0 7 * * *", null)]
    [InlineData("@daily", null)]
    [InlineData("not a cron", "Schedule must be")]
    public void A_schedule_is_checked_when_it_is_saved(string cron, string? expected)
    {
        var problem = ScheduledQueries.ScheduleProblem(cron);
        if (expected is null) Assert.Null(problem);
        else Assert.StartsWith(expected, problem);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("https://example.com/hook", null)]
    [InlineData("ftp://example.com/hook", "The URL must be")]
    [InlineData("http://169.254.169.254/latest", "That address is on a private")]
    public void A_destination_is_checked_when_it_is_saved(string url, string? expected)
    {
        var problem = ScheduledQueries.WebhookProblem(url);
        if (expected is null) Assert.Null(problem);
        else Assert.StartsWith(expected, problem);
    }
}
