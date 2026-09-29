using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class BaseportSourceTests : IDisposable
{
    private const string Origin = "https://93.184.216.34";
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public BaseportSourceTests()
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
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static TableDefinition Published(string apiName, string display = "") => new()
    {
        Id = Ids.NewShortId(12),
        Name = apiName,
        ApiName = apiName,
        ApiEnabled = true,
        ApiDisplayName = display,
        Fields = [new() { Id = Ids.NewShortId(12), TableId = "x", Name = "title", DataType = "text" }]
    };

    [Fact]
    public void DiscoversPublishedTablesFromOurOwnDocument()
    {
        var hidden = Published("secret-table");
        hidden.ApiEnabled = false;
        var document = OpenApiSpec.BuildDocument(new DocumentInputs(
            [Published("orders", "Sales orders"), Published("customers"), hidden],
            new AppSettings { PublicAuthEnabled = true }, "1", [], []));

        var tables = BaseportSource.Tables(document);

        Assert.Equal(new[] { "customers", "orders" }, tables.Select(t => t.ApiName));
        Assert.Equal("Sales orders", tables.Single(t => t.ApiName == "orders").Title);
    }

    [Fact]
    public void EmptyOrForeignDocumentsListNothing()
    {
        Assert.Empty(BaseportSource.Tables(null));
        Assert.Empty(BaseportSource.Tables(new JsonObject { ["paths"] = new JsonObject { ["/pets"] = new JsonObject { ["get"] = new JsonObject() } } }));
    }

    [Fact]
    public async Task WalksRecordsByCursorAndUnwrapsData()
    {
        var stub = new Stub(r =>
        {
            var query = QueryHelpers.ParseQuery(r.RequestUri!.Query);
            var from = query.TryGetValue("cursor", out var c) ? int.Parse(c!) : 0;
            var rows = new JsonArray(Enumerable.Range(from, Math.Min(200, 250 - from)).Select(i => (JsonNode)new JsonObject
            {
                ["id"] = $"rec{i:0000000}",
                ["createdAt"] = "2026-01-01T00:00:00Z",
                ["data"] = new JsonObject { ["title"] = $"t{i}" },
                ["links"] = new JsonObject()
            }).ToArray());
            var body = new JsonObject { ["rows"] = rows, ["nextCursor"] = from + 200 < 250 ? (from + 200).ToString() : null };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        });
        using var http = new HttpClient(stub);
        var connection = new Connection { Id = "c", Name = "peer", BaseUrl = Origin, Protocol = ConnectionProtocols.Baseport };
        var report = new RemoteFetch.Report();
        var rows = new List<JsonObject>();

        await foreach (var page in RemoteFetch.PagesAsync(_db, http, connection, new RemoteFetch.Options("orders", Delay: TimeSpan.Zero), report, TestContext.Current.CancellationToken))
            rows.AddRange(page.Records);

        Assert.Equal(250, rows.Count);
        Assert.Equal("cursor", report.Strategy);
        Assert.Equal($"{Origin}/api/v1/orders/records?pageSize=200", stub.Seen[0].ToString());
        Assert.Equal("t7", rows[7]["title"]!.GetValue<string>());
        Assert.Equal("rec0000007", rows[7][RemoteFetch.SourceIdField]!.GetValue<string>());
        Assert.False(rows[7].ContainsKey("data"));
        Assert.False(rows[7].ContainsKey("links"));
    }

    [Fact]
    public void RemoteSourceIdFieldIsNotOverwritten()
    {
        var row = RemoteFetch.FromBaseport(new JsonObject { ["id"] = "r1", ["data"] = new JsonObject { [RemoteFetch.SourceIdField] = "mine" } });

        Assert.Equal("mine", row[RemoteFetch.SourceIdField]!.GetValue<string>());
    }
}
