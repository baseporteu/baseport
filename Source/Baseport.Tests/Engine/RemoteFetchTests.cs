using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class RemoteFetchTests : IDisposable
{
    private const string Origin = "https://93.184.216.34";
    private const int Total = 250;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RemoteFetchTests()
    {
        TestSecrets.Ensure();
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Slow : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Json(new JsonArray());
        }
    }

    private static HttpResponseMessage Json(JsonNode body, string? link = null)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (link is not null) resp.Headers.TryAddWithoutValidation("Link", link);
        return resp;
    }

    private static JsonArray Slice(int from, int count) =>
        new(Enumerable.Range(from, Math.Max(0, Math.Min(count, Total - from))).Select(i => (JsonNode)new JsonObject { ["id"] = i, ["name"] = $"row {i}" }).ToArray());

    private static int Q(HttpRequestMessage r, string name, int fallback) =>
        QueryHelpers.ParseQuery(r.RequestUri!.Query).TryGetValue(name, out var v) && int.TryParse(v.ToString(), out var n) ? n : fallback;

    private static Connection Conn(string protocol = "rest") => new() { Id = "c1", Name = "api", BaseUrl = Origin + "/api", Protocol = protocol };

    private async Task<(List<JsonObject> Rows, RemoteFetch.Report Report, Stub Stub)> Run(Func<HttpRequestMessage, HttpResponseMessage> answer,
        string path = "items", string protocol = "rest", RemoteFetch.Options? options = null, Connection? connection = null)
    {
        var stub = new Stub(answer);
        using var http = new HttpClient(stub);
        var report = new RemoteFetch.Report();
        var rows = new List<JsonObject>();
        var opts = (options ?? new RemoteFetch.Options(path)) with { Delay = TimeSpan.Zero };
        await foreach (var page in RemoteFetch.PagesAsync(_db, http, connection ?? Conn(protocol), opts, report, TestContext.Current.CancellationToken))
            rows.AddRange(page.Records);
        return (rows, report, stub);
    }

    private static void AllRows(List<JsonObject> rows) =>
        Assert.Equal(Enumerable.Range(0, Total), rows.Select(r => r["id"]!.GetValue<int>()));

    [Fact]
    public async Task ODataNextLink()
    {
        var (rows, report, _) = await Run(r =>
        {
            var skip = Q(r, "$skip", 0);
            var body = new JsonObject { ["value"] = Slice(skip, 100) };
            if (skip + 100 < Total) body["@odata.nextLink"] = $"{Origin}/api/Items?$top=100&$skip={skip + 100}";
            return Json(body);
        }, "Items", "odata");

        AllRows(rows);
        Assert.Equal("odata", report.Strategy);
        Assert.Equal(3, report.Pages);
    }

    [Fact]
    public async Task ODataSkipWithoutNextLink()
    {
        var (rows, _, stub) = await Run(r => Json(new JsonObject { ["value"] = Slice(Q(r, "$skip", 0), Q(r, "$top", 999)) }), "Items", "odata");

        AllRows(rows);
        Assert.Contains("$top=100", stub.Seen[0].RequestUri!.Query);
    }

    [Fact]
    public async Task LinkHeader()
    {
        var (rows, report, _) = await Run(r =>
        {
            var page = Q(r, "page", 1);
            var link = page * 100 < Total ? $"<{Origin}/api/items?page={page + 1}>; rel=\"next\", <{Origin}/api/items?page=3>; rel=\"last\"" : null;
            return Json(Slice((page - 1) * 100, 100), link);
        });

        AllRows(rows);
        Assert.Equal("link", report.Strategy);
    }

    [Fact]
    public async Task RelativeNextInBody()
    {
        var (rows, report, _) = await Run(r =>
        {
            var page = Q(r, "page", 1);
            return Json(new JsonObject
            {
                ["data"] = Slice((page - 1) * 100, 100),
                ["links"] = new JsonObject { ["next"] = page * 100 < Total ? $"/api/items?page={page + 1}" : null }
            });
        });

        AllRows(rows);
        Assert.Equal("next", report.Strategy);
    }

    [Fact]
    public async Task CursorInBody()
    {
        var (rows, report, _) = await Run(r =>
        {
            var from = QueryHelpers.ParseQuery(r.RequestUri!.Query).TryGetValue("cursor", out var c) ? int.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(c!))) : 0;
            var next = from + 100 < Total ? Convert.ToBase64String(Encoding.UTF8.GetBytes((from + 100).ToString())) : null;
            return Json(new JsonObject { ["rows"] = Slice(from, 100), ["nextCursor"] = next });
        });

        AllRows(rows);
        Assert.Equal("cursor", report.Strategy);
    }

    [Fact]
    public async Task PageNumberWithTotalPages()
    {
        var (rows, report, _) = await Run(r =>
        {
            var page = Q(r, "page", 1);
            return Json(new JsonObject { ["items"] = Slice((page - 1) * 100, 100), ["meta"] = new JsonObject { ["current_page"] = page, ["last_page"] = 3 } });
        });

        AllRows(rows);
        Assert.Equal("page", report.Strategy);
    }

    [Fact]
    public async Task OffsetWithTotal()
    {
        var (rows, report, _) = await Run(r =>
        {
            var offset = Q(r, "offset", 0);
            return Json(new JsonObject { ["results"] = Slice(offset, 100), ["offset"] = offset, ["limit"] = 100, ["total"] = Total });
        });

        AllRows(rows);
        Assert.Equal("offset", report.Strategy);
    }

    [Fact]
    public async Task SinglePage()
    {
        var (rows, report, stub) = await Run(_ => Json(Slice(0, Total)));

        AllRows(rows);
        Assert.Single(stub.Seen);
        Assert.Equal(1, report.Pages);
    }

    [Fact]
    public async Task NestedDataAndExplicitPointer()
    {
        var nested = await Run(_ => Json(new JsonObject { ["data"] = new JsonObject { ["items"] = Slice(0, Total) } }));
        AllRows(nested.Rows);

        var pointed = await Run(_ => Json(new JsonObject { ["payload"] = new JsonObject { ["list"] = Slice(0, Total) } }),
            options: new RemoteFetch.Options("items", RecordsPointer: "/payload/list"));
        AllRows(pointed.Rows);
    }

    [Fact]
    public async Task NoRecordListIsAnError()
    {
        var ex = await Assert.ThrowsAsync<RemoteFetch.FetchException>(() => Run(_ => Json(new JsonObject { ["message"] = "hi" })));
        Assert.Contains("records path", ex.Message);
    }

    [Fact]
    public async Task ServerErrorMidRunIsAnError()
    {
        var ex = await Assert.ThrowsAsync<RemoteFetch.FetchException>(() => Run(r =>
            Q(r, "page", 1) == 2 ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(new JsonObject { ["items"] = Slice(0, 100), ["page"] = 1, ["totalPages"] = 3 })));

        Assert.Contains("500", ex.Message);
        Assert.DoesNotContain("page=2", ex.Message);
    }

    [Fact]
    public async Task CrossOriginNextStopsBeforeSendingCredentials()
    {
        var id = Ids.NewShortId(12);
        _db.Secrets.Add(new Secret { Id = id, Name = "tok", ValueProtected = Secrets.Protect("bearer-plain"), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var connection = Conn();
        connection.AuthKind = "bearer";
        connection.AuthSecretId = id;
        var stub = new Stub(_ => Json(new JsonObject { ["items"] = Slice(0, 100), ["next"] = "https://evil.example/steal" }));
        using var http = new HttpClient(stub);

        var ex = await Assert.ThrowsAsync<RemoteFetch.FetchException>(async () =>
        {
            await foreach (var _ in RemoteFetch.PagesAsync(_db, http, connection, new RemoteFetch.Options("items", Delay: TimeSpan.Zero), new RemoteFetch.Report(), TestContext.Current.CancellationToken)) { }
        });

        Assert.Contains("another origin", ex.Message);
        Assert.All(stub.Seen, r => Assert.Equal("93.184.216.34", r.RequestUri!.Host));
    }

    [Fact]
    public async Task RepeatedNextStops()
    {
        var (_, report, stub) = await Run(_ => Json(new JsonObject { ["items"] = Slice(0, 10), ["next"] = "/api/items?page=1" }), "items?page=1");

        Assert.NotNull(report.Stopped);
        Assert.Single(stub.Seen);
    }

    [Fact]
    public async Task SamePageUnderAnotherUrlStops()
    {
        var (rows, report, stub) = await Run(_ => Json(new JsonObject { ["items"] = Slice(0, 10), ["next"] = "/api/items?page=1" }));

        Assert.NotNull(report.Stopped);
        Assert.Equal(2, stub.Seen.Count);
        Assert.Equal(10, rows.Count);
    }

    [Fact]
    public async Task DetectedStrategyHoldsForTheWholeWalk()
    {
        var (rows, _, stub) = await Run(r =>
        {
            var cursor = Q(r, "cursor", 0);
            var body = new JsonObject { ["items"] = Slice(cursor, 200), ["page"] = 1, ["totalPages"] = 2 };
            if (cursor == 0) body["links"] = new JsonObject { ["next"] = "/api/items?cursor=200" };
            return Json(body);
        });

        Assert.Equal(2, stub.Seen.Count);
        Assert.Equal(Total, rows.Count);
    }

    [Fact]
    public async Task RowCeiling()
    {
        var (rows, report, _) = await Run(r => Json(new JsonObject { ["items"] = Slice(Q(r, "offset", 0), 100), ["offset"] = Q(r, "offset", 0), ["total"] = Total }),
            options: new RemoteFetch.Options("items", MaxRows: 150));

        Assert.Equal(150, rows.Count);
        Assert.Contains("150", report.Ceiling);
    }

    [Fact]
    public async Task PageCeiling()
    {
        var (rows, report, _) = await Run(r => Json(new JsonObject { ["items"] = Slice(Q(r, "offset", 0), 100), ["offset"] = Q(r, "offset", 0), ["total"] = Total }),
            options: new RemoteFetch.Options("items", MaxPages: 2));

        Assert.Equal(200, rows.Count);
        Assert.NotNull(report.Ceiling);
    }

    [Fact]
    public async Task OversizedPage()
    {
        var huge = new string('x', RemoteFetch.MaxBytesPerPage + 10);

        var ex = await Assert.ThrowsAsync<RemoteFetch.FetchException>(() => Run(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"[\"{huge}\"]") }));

        Assert.Contains("MB", ex.Message);
    }

    [Fact]
    public async Task NonJson()
    {
        await Assert.ThrowsAsync<RemoteFetch.FetchException>(() => Run(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") }));
    }

    [Fact]
    public async Task Timeout()
    {
        using var http = new HttpClient(new Slow());
        var report = new RemoteFetch.Report();

        var ex = await Assert.ThrowsAsync<RemoteFetch.FetchException>(async () =>
        {
            await foreach (var _ in RemoteFetch.PagesAsync(_db, http, Conn(), new RemoteFetch.Options("items", Timeout: TimeSpan.FromMilliseconds(200)), report, TestContext.Current.CancellationToken)) { }
        });

        Assert.Contains("did not answer", ex.Message);
    }

    [Fact]
    public async Task EveryPageCarriesTheCredential()
    {
        var id = Ids.NewShortId(12);
        _db.Secrets.Add(new Secret { Id = id, Name = "tok2", ValueProtected = Secrets.Protect("k-1"), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var connection = Conn();
        connection.AuthKind = "header";
        connection.AuthHeaderName = "X-Api-Key";
        connection.AuthSecretId = id;

        var (_, _, stub) = await Run(r =>
        {
            var page = Q(r, "page", 1);
            return Json(new JsonObject { ["items"] = Slice((page - 1) * 100, 100), ["page"] = page, ["totalPages"] = 3 });
        }, connection: connection);

        Assert.Equal(3, stub.Seen.Count);
        Assert.All(stub.Seen, r => Assert.Equal("k-1", r.Headers.GetValues("X-Api-Key").Single()));
    }

    [Theory]
    [InlineData("<https://a/p?page=2>; rel=\"next\"", "https://a/p?page=2")]
    [InlineData("<https://a/p?page=1>; rel=\"prev\", <https://a/p?page=3>; rel=next", "https://a/p?page=3")]
    [InlineData("<https://a/p?page=3>; rel=\"last\"", null)]
    [InlineData("", null)]
    public void LinkHeaderParsing(string header, string? next)
    {
        Assert.Equal(next, RemoteFetch.LinkNext(header));
    }

    [Fact]
    public void NumericNextIsNotAUrl()
    {
        var next = RemoteFetch.Detect(new JsonObject { ["items"] = Slice(0, 1), ["next"] = "2" }, new Uri(Origin + "/api/items"), null, 1, "auto", "rest", 100);

        Assert.Null(next);
    }
}
