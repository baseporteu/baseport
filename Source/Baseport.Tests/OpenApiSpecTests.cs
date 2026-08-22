using System.Text.Json.Nodes;
using Xunit;
using Baseport;

namespace Baseport.Tests;

// The OpenAPI document is a promise to every generated client, the error contract it describes has to match the one the endpoints actually return: every operation documents the standard error set, and every error response references the one Error schema instead of inlining a private shape.
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

    // A client watching one row needs to find the route in the document, not guess it from the table one.
    [Fact]
    public void The_single_record_stream_is_published_beside_the_table_stream()
    {
        var op = (Paths(Table())["/api/v1/orders/subscribe/{recordId}"] as JsonObject)?["get"] as JsonObject;
        Assert.NotNull(op);
        Assert.NotNull(op!["responses"]!["200"]!["content"]!["text/event-stream"]);
        AssertErrorRef((op["responses"] as JsonObject)!, "404");
    }

    [Fact]
    public void The_stream_follows_the_GET_switch()
    {
        var t = Table();
        t.ApiMethods = "POST";
        Assert.Null(Paths(t)["/api/v1/orders/subscribe"]);
        Assert.Null(Paths(t)["/api/v1/orders/subscribe/{recordId}"]);
    }

    [Fact]
    public void A_proxy_table_publishes_no_stream()
    {
        // It stores nothing locally, there is nothing to emit a change for.
        var t = Table();
        t.IsProxy = true;
        Assert.Null(Paths(t)["/api/v1/orders/subscribe"]);
        Assert.Null(Paths(t)["/api/v1/orders/subscribe/{recordId}"]);
    }

    private static void AssertErrorRef(JsonObject responses, string code)
    {
        var resp = responses[code] as JsonObject;
        Assert.NotNull(resp);
        Assert.NotNull(resp!["description"]);
        var schema = resp!["content"]![ApiProblems.ContentType]!["schema"] as JsonObject;
        Assert.Equal("#/components/schemas/Error", schema?["$ref"]?.GetValue<string>());
    }

    [Fact]
    public void Every_operation_documents_401_403_404_500_via_the_shared_error_schema()
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
            // 403 is what an access rule answers with, and it went undeclared while five call sites returned it.
            AssertErrorRef(responses, "403");
            AssertErrorRef(responses, "404");
            AssertErrorRef(responses, "500");
        }
    }

    // A read reaches 400 through a malformed expand, which is why it is declared there too; a delete parses nothing a caller wrote and still must not claim it.
    [Fact]
    public void Only_the_operations_that_parse_caller_input_document_400()
    {
        var t = Table();
        var paths = Paths(t);
        var list = paths["/api/v1/orders/records"] as JsonObject;
        var item = paths["/api/v1/orders/records/{recordId}"] as JsonObject;

        foreach (var responses in new[]
                 {
                     list!["get"]!["responses"] as JsonObject,
                     list["post"]!["responses"] as JsonObject,
                     item!["get"]!["responses"] as JsonObject,
                     item["patch"]!["responses"] as JsonObject,
                     item["put"]!["responses"] as JsonObject
                 })
            Assert.True(responses!.ContainsKey("400"), "an operation that parses caller input should document 400");

        var deleteResponses = item["delete"]!["responses"] as JsonObject;
        Assert.False(deleteResponses!.ContainsKey("400"), "delete must not document a 400 it cannot return");
    }

    // A write refuses a duplicate with 409 and a bad value with 422, and the document has to name both or a client cannot tell a retry from a fix.
    [Fact]
    public void A_write_documents_the_conflict_and_validation_statuses_it_returns()
    {
        var paths = Paths(Table());
        var list = paths["/api/v1/orders/records"] as JsonObject;
        var item = paths["/api/v1/orders/records/{recordId}"] as JsonObject;

        foreach (var responses in new[]
                 {
                     list!["post"]!["responses"] as JsonObject,
                     item!["patch"]!["responses"] as JsonObject,
                     item["put"]!["responses"] as JsonObject
                 })
        {
            AssertErrorRef(responses!, "409");
            AssertErrorRef(responses!, "412");
            AssertErrorRef(responses!, "413");
            AssertErrorRef(responses!, "415");
            AssertErrorRef(responses!, "422");
        }

        // Delete writes no body, so only the precondition applies to it.
        var del = item["delete"]!["responses"] as JsonObject;
        AssertErrorRef(del!, "412");
        Assert.False(del!.ContainsKey("422"), "delete validates no record and must not claim 422");
    }

    [Fact]
    public void A_versioned_response_carries_an_ETag_and_the_write_takes_If_Match()
    {
        var paths = Paths(Table());
        var item = paths["/api/v1/orders/records/{recordId}"] as JsonObject;

        Assert.NotNull(item!["get"]!["responses"]!["200"]!["headers"]!["ETag"]);
        Assert.NotNull(item["patch"]!["responses"]!["200"]!["headers"]!["ETag"]);
        Assert.NotNull((paths["/api/v1/orders/records"] as JsonObject)!["post"]!["responses"]!["201"]!["headers"]!["Location"]);

        // A caller cannot send a precondition it was never told about.
        foreach (var method in new[] { "patch", "put", "delete" })
        {
            var parameters = item[method]!["parameters"] as JsonArray;
            Assert.Contains(parameters!, n => n!["name"]!.GetValue<string>() == "If-Match" && n["in"]!.GetValue<string>() == "header");
        }
        Assert.Contains((item["get"]!["parameters"] as JsonArray)!, n => n!["name"]!.GetValue<string>() == "If-None-Match");
    }

    [Fact]
    public void The_listing_publishes_its_cursor_in_both_directions()
    {
        var list = (Paths(Table())["/api/v1/orders/records"] as JsonObject)!["get"]!;
        Assert.Contains((list["parameters"] as JsonArray)!, n => n!["name"]!.GetValue<string>() == "cursor");
        var page = list["responses"]!["200"]!["content"]!["application/json"]!["schema"] as JsonObject;
        Assert.NotNull(page!["properties"]!["nextCursor"]);
    }

    // RFC 9457: the members a client keys off must be there, and the extensions the console, embed and SDK already read must survive beside them.
    [Fact]
    public void The_error_schema_is_a_problem_document_that_kept_its_extension_members()
    {
        var schema = ErrorSchema();
        foreach (var member in new[] { "type", "title", "status", "detail", "instance" })
            Assert.NotNull(schema!["properties"]![member]);

        var required = schema!["required"] as JsonArray;
        foreach (var member in new[] { "type", "title", "status", "errors" })
            Assert.Contains(required!, n => n!.GetValue<string>() == member);
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
        // /docs renders this to anyone who asks, the internal name and the internal id must not travel with it.
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

        Assert.False(props.ContainsKey("Margin"), "a calculated field cannot be written, it must not appear in the request schema");
        Assert.True(props.ContainsKey("OrderNo"));
    }

    [Fact]
    public void A_field_transports_its_label_help_text_and_constraints()
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
        // The public API always includes links, and includes expanded whenever expand asked for one, the fullest shape is what the document has to describe.
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
            Assert.Contains((operation!["security"] as JsonArray)!, s => s!.AsObject().ContainsKey(OpenApiSpec.SecurityScheme));
    }

    // Scalar labels a scheme by its key, so this string is what a reader sees in the auth panel. docs.html has to preselect the same one; that pair is pinned in Scripts/test-frontend.js, which reads the working tree.
    [Fact]
    public void The_security_scheme_is_named_the_way_the_reference_renders_it() =>
        Assert.Equal("Bearer", OpenApiSpec.SecurityScheme);

    // A sigil in a parameter name promises semantics this API does not implement, and "$" in a double-quoted shell string is dropped before curl ever sees it.
    [Fact]
    public void No_query_parameter_carries_a_sigil()
    {
        var list = (OpenApiSpec.BuildPaths(new List<TableDefinition> { Detailed() })["/api/v1/sales-orders/records"]!["get"]!["parameters"] as JsonArray)!;
        foreach (var p in list)
        {
            var name = p!["name"]!.GetValue<string>();
            Assert.Matches("^[a-zA-Z][a-zA-Z0-9]*$", name);
        }
        Assert.Equal("expand", ApiLinks.ExpandParameter);
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
        // Operations reference the tag by name, the two must not diverge.
        var op = OpenApiSpec.BuildPaths(new List<TableDefinition> { t })["/api/v1/sales-orders/records"]!["get"]!;
        Assert.Contains((op["tags"] as JsonArray)!, n => n!.GetValue<string>() == "sales-orders");
        Assert.Contains("Sales orders", op["summary"]!.GetValue<string>());
    }

    [Fact]
    public void Author_markdown_becomes_the_tag_description()
    {
        var t = Detailed();
        t.ApiDocumentation = "## Identifiers\n\nEvery order includes an `OrderNo`.";

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
        // The bug this guards: a renderer that honours tag groups hides every tag missing from them, grouping one table would hide the others.
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

    // The published schema is the contract: an object field that collapsed to "type":"string" was a promise no client could keep.
    [Fact]
    public void A_nested_object_field_publishes_its_members_not_a_string()
    {
        var table = Table();
        table.Fields.Add(new FieldDefinition
        {
            Id = Ids.NewShortId(12),
            TableId = "x",
            Name = "address",
            DataType = "json",
            OptionsJson = """{"fields":[{"name":"street","dataType":"text","isRequired":true},{"name":"email","dataType":"email"}]}"""
        });

        var schema = (OpenApiSpec.BuildSchemas(new List<TableDefinition> { table })["Orders"] as JsonObject)!;
        var address = (schema["properties"]!["address"] as JsonObject)!;

        Assert.Equal("object", address["type"]!.GetValue<string>());
        Assert.Equal("string", address["properties"]!["street"]!["type"]!.GetValue<string>());
        Assert.Equal("email", address["properties"]!["email"]!["format"]!.GetValue<string>());
        Assert.Equal(new[] { "street" }, (address["required"] as JsonArray)!.Select(v => v!.GetValue<string>()));
        Assert.False(address["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void A_list_field_publishes_its_item_schema()
    {
        var table = Table();
        table.Fields.Add(new FieldDefinition
        {
            Id = Ids.NewShortId(12), TableId = "x", Name = "lines", DataType = "array",
            OptionsJson = """{"fields":[{"name":"qty","dataType":"number"}]}"""
        });
        table.Fields.Add(new FieldDefinition
        {
            Id = Ids.NewShortId(12), TableId = "x", Name = "tags", DataType = "multiselect",
            OptionsJson = """["a","b"]"""
        });

        var props = (OpenApiSpec.BuildSchemas(new List<TableDefinition> { table })["Orders"]!["properties"] as JsonObject)!;

        Assert.Equal("array", props["lines"]!["type"]!.GetValue<string>());
        Assert.Equal("number", props["lines"]!["items"]!["properties"]!["qty"]!["type"]!.GetValue<string>());
        Assert.Equal("array", props["tags"]!["type"]!.GetValue<string>());
        Assert.Equal(new[] { "a", "b" }, (props["tags"]!["items"]!["enum"] as JsonArray)!.Select(v => v!.GetValue<string>()));
    }
}
