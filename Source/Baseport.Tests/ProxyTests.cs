using Xunit;
using System.Text.Json.Nodes;
using Baseport;

namespace Baseport.Tests;

// Proxy imports rely on reading a live record because real specs routinely declare every body as a bare {"type":"object"}.
public class ProxyTests
{
    [Theory]
    [InlineData("""{"value":[{"sku":"P001"},{"sku":"P002"}]}""")]
    [InlineData("""{"data":[{"sku":"P001"}]}""")]
    [InlineData("""{"items":[{"sku":"P001"}]}""")]
    [InlineData("""{"results":[{"sku":"P001"}]}""")]
    [InlineData("""[{"sku":"P001"}]""")]
    public void FirstRecord_unwraps_the_common_collection_envelopes(string json)
    {
        var record = OpenApiProxy.FirstRecord(JsonNode.Parse(json));
        Assert.NotNull(record);
        Assert.Equal("P001", record!["sku"]!.GetValue<string>());
    }

    [Fact]
    public void FirstRecord_treats_a_bare_object_as_the_record()
    {
        var record = OpenApiProxy.FirstRecord(JsonNode.Parse("""{"sku":"P001","name":"Widget"}"""));
        Assert.NotNull(record);
        Assert.Equal("Widget", record!["name"]!.GetValue<string>());
    }

    [Fact]
    public void FirstRecord_returns_null_for_an_empty_collection()
    {
        Assert.Null(OpenApiProxy.FirstRecord(JsonNode.Parse("""{"value":[]}""")));
        Assert.Null(OpenApiProxy.FirstRecord(JsonNode.Parse("[]")));
    }

    [Fact]
    public void Records_returns_every_row_of_a_collection()
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
    public void Sampled_json_types_map_to_field_types(string type, string format, string expected)
    {
        var prop = new OpenApiProxy.FieldProp("X", type, format, new List<string>(), false);
        Assert.Equal(expected, OpenApiProxy.MapFieldType(prop));
    }

    [Fact]
    public void An_enum_property_becomes_a_select_regardless_of_its_type()
    {
        var prop = new OpenApiProxy.FieldProp("Status", "string", "", new List<string> { "open", "closed" }, false);
        Assert.Equal("select", OpenApiProxy.MapFieldType(prop));
    }

    [Fact]
    public void CanRead_is_false_until_a_read_endpoint_is_known()
    {
        Assert.False(ProxyQuery.CanRead(new TableDefinition { IsProxy = true }));
        Assert.True(ProxyQuery.CanRead(new TableDefinition { IsProxy = true, ProxyReadUrl = "https://example.test/items" }));
    }

    [Fact]
    public void Project_reveals_only_the_configured_fields_of_a_remote_record()
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
    public void Project_emits_a_null_for_a_field_the_remote_omitted()
    {
        var remote = (JsonObject)JsonNode.Parse("""{"sku":"P001"}""")!;
        var visible = new List<FieldDefinition> { new() { Name = "sku" }, new() { Name = "name" } };

        var projected = ProxyQuery.Project(remote, visible);

        Assert.True(projected.ContainsKey("name"));
        Assert.Null(projected["name"]);
    }

    [Fact]
    public void TryParseError_surfaces_the_remote_message()
    {
        Assert.Equal("Authentication required", OpenApiProxy.TryParseError("""{"error":"Authentication required"}"""));
        Assert.Equal("Bad request", OpenApiProxy.TryParseError("""{"title":"Bad request"}"""));
        Assert.Null(OpenApiProxy.TryParseError("not json"));
    }

    [Fact]
    public void A_proxied_list_is_sorted_the_form_configures_it_to_be()
    {
        // sortField/descending were computed for every list but only ever passed to the local (non-proxy) query path, a proxied list's configured sort was silently dropped.
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
        // Amount is stored as a string by the remote, a text sort would put "10" < "20" < "30" wrong only for two-digit vs three-digit values; still worth locking in numeric comparison.
        Assert.Equal(new[] { "10", "20", "30" },
                     ProxyQuery.Sorted(records, amount, descending: false).Select(r => r["Amount"]!.GetValue<string>()));
    }

    [Fact]
    public void An_unset_sort_field_leaves_a_proxied_list_in_its_remote_order()
    {
        var records = new List<JsonObject>
        {
            (JsonObject)JsonNode.Parse("""{"Name":"Charlie"}""")!,
            (JsonObject)JsonNode.Parse("""{"Name":"Alice"}""")!
        };
        Assert.Equal(new[] { "Charlie", "Alice" }, ProxyQuery.Sorted(records, null, false).Select(r => r["Name"]!.GetValue<string>()));
    }

    // A proxy target is a url an operator types and the server fetches from inside its own network, it is the one place an ssrf reaches cloud metadata or a neighbour's admin port.
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
    public void A_private_or_non_http_proxy_target_is_refused(string url)
    {
        ProxyTarget.Configure(new AppSettings());
        Assert.NotNull(ProxyTarget.Problem(url));
    }

    [Fact]
    public void A_public_proxy_target_is_allowed()
    {
        ProxyTarget.Configure(new AppSettings());
        Assert.Null(ProxyTarget.Problem("https://93.184.216.34/openapi.json"));
    }

    // The intended target often is local (a Portway on the same host), the block is a default an operator can lift, not a wall.
    [Fact]
    public void An_operator_can_open_private_targets()
    {
        ProxyTarget.Configure(new AppSettings { ProxyPrivateTargetsEnabled = true });
        Assert.Null(ProxyTarget.Problem("http://127.0.0.1:5000/api/openapi.json"));

        ProxyTarget.Configure(new AppSettings());
        Assert.NotNull(ProxyTarget.Problem("http://127.0.0.1:5000/api/openapi.json"));
    }

    // A scheme is refused whatever the setting says: only http(s) is ever fetched.
    [Fact]
    public void Opening_private_targets_does_not_open_other_schemes()
    {
        ProxyTarget.Configure(new AppSettings { ProxyPrivateTargetsEnabled = true });
        Assert.NotNull(ProxyTarget.Problem("file:///etc/passwd"));
        ProxyTarget.Configure(new AppSettings());
    }
}
