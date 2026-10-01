using Xunit;
using System.Text.Json.Nodes;
using Baseport;

namespace Baseport.Tests;

public class ProxyTests
{
    [Theory]
    [InlineData("""{"value":[{"sku":"P001"},{"sku":"P002"}]}""")]
    [InlineData("""{"data":[{"sku":"P001"}]}""")]
    [InlineData("""{"items":[{"sku":"P001"}]}""")]
    [InlineData("""{"results":[{"sku":"P001"}]}""")]
    [InlineData("""[{"sku":"P001"}]""")]
    public void FirstRecordUnwrapsEnvelope(string json)
    {
        var record = OpenApiProxy.FirstRecord(JsonNode.Parse(json));
        Assert.NotNull(record);
        Assert.Equal("P001", record!["sku"]!.GetValue<string>());
    }

    [Fact]
    public void FirstRecordAcceptsObject()
    {
        var record = OpenApiProxy.FirstRecord(JsonNode.Parse("""{"sku":"P001","name":"Widget"}"""));
        Assert.NotNull(record);
        Assert.Equal("Widget", record!["name"]!.GetValue<string>());
    }

    [Fact]
    public void FirstRecordNullWhenEmpty()
    {
        Assert.Null(OpenApiProxy.FirstRecord(JsonNode.Parse("""{"value":[]}""")));
        Assert.Null(OpenApiProxy.FirstRecord(JsonNode.Parse("[]")));
    }

    [Fact]
    public void RecordsReturnsAllRows()
    {
        var rows = OpenApiProxy.Records(JsonNode.Parse("""{"value":[{"a":1},{"a":2},{"a":3}]}"""));
        Assert.Equal(3, rows.Count);
    }

    [Theory]
    [InlineData("number", "", "number")]
    [InlineData("integer", "", "number")]
    [InlineData("boolean", "", "boolean")]
    [InlineData("string", "", "text")]
    [InlineData("string", "date", "date")]
    [InlineData("string", "date-time", "datetime")]
    [InlineData("object", "", "json")]
    [InlineData("array", "", "array")]
    [InlineData("string", "email", "email")]
    [InlineData("string", "uri", "url")]
    public void SampledTypesMapToFields(string type, string format, string expected)
    {
        var prop = new OpenApiProxy.FieldProp("X", type, format, new List<string>(), false);
        Assert.Equal(expected, OpenApiProxy.MapFieldType(prop));
    }

    [Fact]
    public void EnumBecomesSelect()
    {
        var prop = new OpenApiProxy.FieldProp("Status", "string", "", new List<string> { "open", "closed" }, false);
        Assert.Equal("select", OpenApiProxy.MapFieldType(prop));
    }

    [Fact]
    public void CanReadNeedsEndpoint()
    {
        Assert.False(ProxyQuery.CanRead(new TableDefinition { IsProxy = true }));
        Assert.True(ProxyQuery.CanRead(new TableDefinition { IsProxy = true, ProxyReadUrl = "https://example.test/items" }));
    }

    [Fact]
    public void ProjectKeepsConfiguredFields()
    {
        var remote = (JsonObject)JsonNode.Parse("""{"sku":"P001","name":"Widget","costPrice":9.99}""")!;
        var visible = new List<FieldDefinition>
        {
            new() { Name = "sku", DataType = "text" },
            new() { Name = "name", DataType = "text" }
        };

        var projected = ProxyQuery.Project(remote, visible);

        Assert.True(projected.ContainsKey("sku"));
        Assert.True(projected.ContainsKey("name"));
        Assert.False(projected.ContainsKey("costPrice"));
    }

    [Fact]
    public void ProjectNullsMissingFields()
    {
        var remote = (JsonObject)JsonNode.Parse("""{"sku":"P001"}""")!;
        var visible = new List<FieldDefinition> { new() { Name = "sku" }, new() { Name = "name" } };

        var projected = ProxyQuery.Project(remote, visible);

        Assert.True(projected.ContainsKey("name"));
        Assert.Null(projected["name"]);
    }

    [Fact]
    public void TryParseErrorKeepsMessage()
    {
        Assert.Equal("Authentication required", OpenApiProxy.TryParseError("""{"error":"Authentication required"}"""));
        Assert.Equal("Bad request", OpenApiProxy.TryParseError("""{"title":"Bad request"}"""));
        Assert.Null(OpenApiProxy.TryParseError("not json"));
    }

    [Fact]
    public void ProxyListIsSorted()
    {

        var records = new List<JsonObject>
        {
            (JsonObject)JsonNode.Parse("""{"Name":"Charlie","Amount":"30"}""")!,
            (JsonObject)JsonNode.Parse("""{"Name":"Alice","Amount":"10"}""")!,
            (JsonObject)JsonNode.Parse("""{"Name":"Bob","Amount":"20"}""")!
        };
        var name = new FieldDefinition { Name = "Name", DataType = "text" };
        var amount = new FieldDefinition { Name = "Amount", DataType = "number" };

        Assert.Equal(new[] { "Alice", "Bob", "Charlie" },
                     ProxyQuery.Sorted(records, name, descending: false).Select(r => r["Name"]!.GetValue<string>()));
        Assert.Equal(new[] { "Charlie", "Bob", "Alice" },
                     ProxyQuery.Sorted(records, name, descending: true).Select(r => r["Name"]!.GetValue<string>()));

        Assert.Equal(new[] { "10", "20", "30" },
                     ProxyQuery.Sorted(records, amount, descending: false).Select(r => r["Amount"]!.GetValue<string>()));
    }

    [Fact]
    public void ProxyListKeepsRemoteOrder()
    {
        var records = new List<JsonObject>
        {
            (JsonObject)JsonNode.Parse("""{"Name":"Charlie"}""")!,
            (JsonObject)JsonNode.Parse("""{"Name":"Alice"}""")!
        };
        Assert.Equal(new[] { "Charlie", "Alice" }, ProxyQuery.Sorted(records, null, false).Select(r => r["Name"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:5000/api/openapi.json")]
    [InlineData("http://localhost:5000/api/openapi.json")]
    [InlineData("http://10.0.0.5/spec.json")]
    [InlineData("http://192.168.1.10/spec.json")]
    [InlineData("http://172.16.4.4/spec.json")]
    [InlineData("http://[::1]/spec.json")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/spec.json")]
    [InlineData("not a url")]
    public void PrivateTargetRefused(string url)
    {
        ProxyTarget.Configure(new AppSettings());
        Assert.NotNull(ProxyTarget.Problem(url));
    }

    [Fact]
    public void PublicTargetAllowed()
    {
        ProxyTarget.Configure(new AppSettings());
        Assert.Null(ProxyTarget.Problem("https://93.184.216.34/openapi.json"));
    }

    [Fact]
    public void OperatorCanAllowPrivate()
    {
        ProxyTarget.Configure(new AppSettings { ProxyPrivateTargetsEnabled = true });
        Assert.Null(ProxyTarget.Problem("http://127.0.0.1:5000/api/openapi.json"));

        ProxyTarget.Configure(new AppSettings());
        Assert.NotNull(ProxyTarget.Problem("http://127.0.0.1:5000/api/openapi.json"));
    }

    [Fact]
    public void PrivateDoesNotOpenSchemes()
    {
        ProxyTarget.Configure(new AppSettings { ProxyPrivateTargetsEnabled = true });
        Assert.NotNull(ProxyTarget.Problem("file:///etc/passwd"));
        ProxyTarget.Configure(new AppSettings());
    }
}
