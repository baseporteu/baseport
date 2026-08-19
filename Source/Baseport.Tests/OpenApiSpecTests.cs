using System.Text.Json.Nodes;
using Xunit;
using Baseport;

namespace Baseport.Tests;

// The OpenAPI document is a promise to every generated client, so the error contract it describes has to match the one the endpoints actually return: every operation documents the standard error set, and every error response references the one Error schema instead of inlining a private shape.
public class OpenApiSpecTests
{
    private static TableDefinition Table() => new()
    {
        Id = Ids.NewShortId(12),
        Name = "Orders",
        ApiName = "orders",
        ApiEnabled = true,
        Fields = new List<FieldDefinition>
        {
            new() { Id = Ids.NewShortId(12), TableId = "x", Name = "total", DataType = "number", Label = "Total" }
        }
    };

    private static JsonObject Paths(TableDefinition t) => OpenApiSpec.BuildPaths(new List<TableDefinition> { t });

    private static JsonObject Ops(TableDefinition t) => (Paths(t)["/api/v1/orders/records"] as JsonObject)!;

    private static JsonObject ErrorSchema() => (OpenApiSpec.BuildSchemas(new List<TableDefinition> { Table() })["Error"] as JsonObject)!;

    // An endpoint absent from the document is as good as unpublished: a consumer generating a client never learns it exists.
    [Fact]
    public void The_stream_is_published_and_describes_one_event_not_the_body()
    {
        var op = (Paths(Table())["/api/v1/orders/subscribe"] as JsonObject)?["get"] as JsonObject;
        Assert.NotNull(op);

        var item = op!["responses"]!["200"]!["content"]!["text/event-stream"]!["itemSchema"]!;
        Assert.Equal("object", item["type"]!.GetValue<string>());
        Assert.Equal(
            new[] { "create", "update", "delete" },
            (item["properties"]!["action"]!["enum"] as JsonArray)!.Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public void The_stream_follows_the_GET_switch()
    {
        var t = Table();
        t.ApiMethods = "POST";
        Assert.Null(Paths(t)["/api/v1/orders/subscribe"]);
    }

    [Fact]
    public void A_proxy_table_publishes_no_stream()
    {
        // It stores nothing locally, so there is nothing to emit a change for.
        var t = Table();
        t.IsProxy = true;
        Assert.Null(Paths(t)["/api/v1/orders/subscribe"]);
    }

    private static void AssertErrorRef(JsonObject responses, string code)
    {
        var resp = responses[code] as JsonObject;
        Assert.NotNull(resp);
        Assert.NotNull(resp!["description"]);
        var schema = resp!["content"]!["application/json"]!["schema"] as JsonObject;
        Assert.Equal("#/components/schemas/Error", schema?["$ref"]?.GetValue<string>());
    }

    [Fact]
    public void Every_operation_documents_401_404_500_via_the_shared_error_schema()
    {
        var t = Table();
        var paths = Paths(t);
        var methods = new[] { "get", "post" }.Concat(
            (paths["/api/v1/orders/records/{recordId}"] as JsonObject)!.Select(kvp => kvp.Key));
        foreach (var method in methods)
        {
            var responses = paths["/api/v1/orders/records"] is JsonObject list && list[method] != null
                ? (list[method]!["responses"] as JsonObject)!
                : (paths["/api/v1/orders/records/{recordId}"]![method]!["responses"] as JsonObject)!;
            AssertErrorRef(responses, "401");
            AssertErrorRef(responses, "404");
            AssertErrorRef(responses, "500");
        }
    }

    [Fact]
    public void Only_write_operations_document_400()
    {
        var t = Table();
        var paths = Paths(t);
        var list = paths["/api/v1/orders/records"] as JsonObject;
        var item = paths["/api/v1/orders/records/{recordId}"] as JsonObject;

        foreach (var method in new[] { "post" })
        {
            var responses = list![method]!["responses"] as JsonObject;
            Assert.True(responses!.ContainsKey("400"), $"{method} should document 400");
        }
        foreach (var method in new[] { "get" })
        {
            var responses = list![method]!["responses"] as JsonObject;
            Assert.False(responses!.ContainsKey("400"), $"{method} must not document a 400 it cannot return");
        }
        foreach (var method in new[] { "patch", "put" })
        {
            var responses = item![method]!["responses"] as JsonObject;
            Assert.True(responses!.ContainsKey("400"), $"{method} should document 400");
        }
        foreach (var method in new[] { "get", "delete" })
        {
            var responses = item![method]!["responses"] as JsonObject;
            Assert.False(responses!.ContainsKey("400"), $"{method} must not document a 400 it cannot return");
        }
    }

    [Fact]
    public void Error_schema_declares_a_required_string_array_and_optional_invalid_names()
    {
        var schema = ErrorSchema();
        Assert.Equal("object", schema!["type"]?.GetValue<string>());
        Assert.Equal("array", schema["properties"]!["errors"]!["type"]?.GetValue<string>());
        Assert.Equal("string", schema["properties"]!["errors"]!["items"]!["type"]?.GetValue<string>());
        Assert.Equal("array", schema["properties"]!["invalid"]!["type"]?.GetValue<string>());
        Assert.Equal("string", schema["properties"]!["invalid"]!["items"]!["type"]?.GetValue<string>());
        var required = schema["required"] as JsonArray;
        Assert.Contains(required!, n => n!.GetValue<string>() == "errors");
        Assert.DoesNotContain(required!, n => n!.GetValue<string>() == "invalid");
    }

    /* what the document must never leak */

    private static TableDefinition Detailed() => new()
    {
        Id = Ids.NewShortId(12),
        Name = "Internal Orders 2024",
        ApiName = "sales-orders",
        ApiEnabled = true,
        Description = "Customer orders.",
        Fields = new List<FieldDefinition>
        {
            new() { Id = Ids.NewShortId(12), Name = "OrderNo", DataType = "text", IsRequired = true, Label = "Order number", HelpText = "Printed on the invoice." },
            new() { Id = Ids.NewShortId(12), Name = "Total", DataType = "currency", Min = 0, Max = 10000 },
            new() { Id = Ids.NewShortId(12), Name = "Status", DataType = "select", OptionsJson = """["open","closed"]""" },
            new() { Id = Ids.NewShortId(12), Name = "Margin", DataType = "calculated", Expression = "data.Total * 0.2" }
        }
    };

    [Fact]
    public void Only_the_published_name_reaches_the_document()
    {
        // /docs renders this to anyone who asks, so the internal name and the internal id must not travel with it.
        var t = Detailed();
        var tables = new List<TableDefinition> { t };
        var json = new JsonObject
        {
            ["tags"] = OpenApiSpec.BuildTags(tables),
            ["paths"] = OpenApiSpec.BuildPaths(tables),
            ["schemas"] = OpenApiSpec.BuildSchemas(tables)
        }.ToJsonString();

        Assert.DoesNotContain("Internal Orders 2024", json);
        Assert.DoesNotContain(t.Id, json);
        Assert.Contains("sales-orders", json);
    }

    [Fact]
    public void Every_documented_path_is_a_public_api_route()
    {
        var paths = OpenApiSpec.BuildPaths(new List<TableDefinition> { Detailed() });

        Assert.NotEmpty(paths);
        foreach (var (path, _) in paths)
            Assert.StartsWith("/api/v1/", path);
    }

    [Fact]
    public void A_derived_field_is_not_offered_as_an_input()
    {
        var props = (OpenApiSpec.BuildSchemas(new List<TableDefinition> { Detailed() })["SalesOrders"]!["properties"] as JsonObject)!;

        Assert.False(props.ContainsKey("Margin"), "a calculated field cannot be written, so it must not appear in the request schema");
        Assert.True(props.ContainsKey("OrderNo"));
    }

    [Fact]
    public void A_field_carries_its_label_help_text_and_constraints()
    {
        // The point of publishing: a consumer should not have to ask what a column means or what it accepts.
        var schema = (OpenApiSpec.BuildSchemas(new List<TableDefinition> { Detailed() })["SalesOrders"] as JsonObject)!;
        var props = (schema["properties"] as JsonObject)!;

        Assert.Equal("Order number", props["OrderNo"]!["title"]!.GetValue<string>());
        Assert.Equal("Printed on the invoice.", props["OrderNo"]!["description"]!.GetValue<string>());
        Assert.Equal(0, props["Total"]!["minimum"]!.GetValue<double>());
        Assert.Equal(10000, props["Total"]!["maximum"]!.GetValue<double>());
        Assert.Equal(new[] { "open", "closed" }, (props["Status"]!["enum"] as JsonArray)!.Select(n => n!.GetValue<string>()));
        Assert.Contains((schema["required"] as JsonArray)!, n => n!.GetValue<string>() == "OrderNo");
    }

    [Fact]
    public void The_documented_record_shape_matches_what_the_api_returns()
    {
        var record = new Record { Id = Ids.NewShortId(12), TableId = "t", JsonData = "{}", CreatedAt = DateTime.UtcNow };
        // The public API always carries links, and carries expanded whenever $expand asked for one, so the fullest shape is what the document has to describe.
        var returned = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
            ApiDtos.RecordDto(record, new List<FieldDefinition>(), new JsonObject(), new JsonObject()),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))!.AsObject();

        var documented = (OpenApiSpec.BuildPaths(new List<TableDefinition> { Detailed() })
            ["/api/v1/sales-orders/records/{recordId}"]!["get"]!["responses"]!["200"]!
            ["content"]!["application/json"]!["schema"]!["properties"] as JsonObject)!;

        Assert.Equal(returned.Select(p => p.Key).OrderBy(k => k), documented.Select(p => p.Key).OrderBy(k => k));
    }

    [Fact]
    public void Every_operation_requires_a_bearer_token()
    {
        foreach (var (_, path) in OpenApiSpec.BuildPaths(new List<TableDefinition> { Detailed() }))
        foreach (var (_, operation) in path!.AsObject())
            Assert.Contains((operation!["security"] as JsonArray)!, s => s!.AsObject().ContainsKey("bearerAuth"));
    }

    /* endpoint documentation and method switches */

    [Fact]
    public void A_disabled_method_is_absent_from_the_document()
    {
        var t = Detailed();
        t.ApiMethods = "GET,POST";

        var paths = OpenApiSpec.BuildPaths(new List<TableDefinition> { t });
        var list = (paths["/api/v1/sales-orders/records"] as JsonObject)!;
        var item = (paths["/api/v1/sales-orders/records/{recordId}"] as JsonObject)!;

        Assert.True(list.ContainsKey("get"));
        Assert.True(list.ContainsKey("post"));
        Assert.True(item.ContainsKey("get"));
        Assert.False(item.ContainsKey("patch"));
        Assert.False(item.ContainsKey("put"));
        Assert.False(item.ContainsKey("delete"));
    }

    [Fact]
    public void A_path_left_with_no_operations_is_not_published_at_all()
    {
        // Reads only: the collection keeps its GET, and the item path keeps its own, but an empty path object would document a route that answers nothing.
        var t = Detailed();
        t.ApiMethods = "POST";

        var paths = OpenApiSpec.BuildPaths(new List<TableDefinition> { t });

        Assert.True(paths.ContainsKey("/api/v1/sales-orders/records"));
        Assert.False(paths.ContainsKey("/api/v1/sales-orders/records/{recordId}"),
                     "the single-record path has no operations left and must be absent");
    }

    [Fact]
    public void The_documentation_name_is_shown_and_the_route_name_still_identifies_the_tag()
    {
        var t = Detailed();
        t.ApiDisplayName = "Sales orders";

        var tag = (OpenApiSpec.BuildTags(new List<TableDefinition> { t })[0] as JsonObject)!;

        Assert.Equal("sales-orders", tag["name"]!.GetValue<string>());
        Assert.Equal("Sales orders", tag["x-displayName"]!.GetValue<string>());
        // Operations reference the tag by name, so the two must not diverge.
        var op = OpenApiSpec.BuildPaths(new List<TableDefinition> { t })["/api/v1/sales-orders/records"]!["get"]!;
        Assert.Contains((op["tags"] as JsonArray)!, n => n!.GetValue<string>() == "sales-orders");
        Assert.Contains("Sales orders", op["summary"]!.GetValue<string>());
    }

    [Fact]
    public void Author_markdown_becomes_the_tag_description()
    {
        var t = Detailed();
        t.ApiDocumentation = "## Identifiers\n\nEvery order carries an `OrderNo`.";

        var tag = (OpenApiSpec.BuildTags(new List<TableDefinition> { t })[0] as JsonObject)!;

        Assert.Equal(t.ApiDocumentation, tag["description"]!.GetValue<string>());
    }

    [Fact]
    public void Without_documentation_the_description_falls_back_rather_than_going_blank()
    {
        var t = Detailed();
        t.ApiDocumentation = "";

        var tag = (OpenApiSpec.BuildTags(new List<TableDefinition> { t })[0] as JsonObject)!;

        Assert.Equal("Customer orders.", tag["description"]!.GetValue<string>());
    }

    [Fact]
    public void Namespaces_become_tag_groups_and_nothing_is_left_out_of_them()
    {
        // The bug this guards: a renderer that honours tag groups hides every tag missing from them, so grouping one table would hide the others.
        var grouped = Detailed();
        grouped.ApiNamespace = "Sales";
        var loose = Detailed();
        loose.ApiName = "customers";
        loose.ApiNamespace = "";

        var groups = OpenApiSpec.BuildTagGroups(new List<TableDefinition> { grouped, loose })!;
        var tagged = groups.SelectMany(g => (g!["tags"] as JsonArray)!.Select(n => n!.GetValue<string>())).ToList();

        Assert.Equal(new[] { "sales-orders", "customers" }.OrderBy(x => x), tagged.OrderBy(x => x));
        Assert.Contains(groups, g => g!["name"]!.GetValue<string>() == "Sales");
    }

    [Fact]
    public void No_namespace_anywhere_means_no_grouping_at_all()
    {
        var t = Detailed();
        t.ApiNamespace = "";

        Assert.Null(OpenApiSpec.BuildTagGroups(new List<TableDefinition> { t }));
    }
}
