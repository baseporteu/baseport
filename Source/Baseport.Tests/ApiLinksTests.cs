using Xunit;
using Baseport;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Baseport.Tests;

// HATEOAS links and $expand: a link is only written for a destination the public API would serve, and an embed refuses exactly what a direct read of the target refuses.
public class ApiLinksTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public ApiLinksTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
    }

    private async Task<TableDefinition> TableAsync(string name, string apiName, bool apiEnabled = true, string readRule = "", string methods = "GET,POST,PATCH,PUT,DELETE")
    {
        var table = new TableDefinition
        {
            Id = Ids.NewShortId(12),
            Name = name,
            ApiName = apiName,
            ApiEnabled = apiEnabled,
            ApiMethods = methods,
            ReadRule = readRule
        };
        _db.Tables.Add(table);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return table;
    }

    private async Task<FieldDefinition> FieldAsync(TableDefinition table, string name, string dataType = "text", string optionsJson = "[]")
    {
        var field = new FieldDefinition { Id = Ids.NewShortId(12), TableId = table.Id, Name = name, DataType = dataType, OptionsJson = optionsJson };
        _db.Fields.Add(field);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return field;
    }

    private async Task<Record> RecordAsync(TableDefinition table, JsonObject data)
    {
        var record = new Record { Id = Ids.NewShortId(12), TableId = table.Id, JsonData = data.ToJsonString(), CreatedAt = DateTime.UtcNow };
        _db.Records.Add(record);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return record;
    }

    // Orders reference customers; both published unless a test says otherwise.
    private async Task<(TableDefinition Orders, List<FieldDefinition> OrderFields, TableDefinition Customers, Record Customer)> ShopAsync(
        bool customersPublished = true, string customersReadRule = "")
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var customers = await TableAsync("Customers", "customers", customersPublished, customersReadRule);
        await FieldAsync(customers, "owner");
        await FieldAsync(customers, "name");
        var customer = await RecordAsync(customers, new JsonObject { ["owner"] = "alice", ["name"] = "Acme" });

        var orders = await TableAsync("Orders", "orders");
        var customerField = await FieldAsync(orders, "customer", "reference", $$"""{ "tableId": "{{customers.Id}}" }""");
        var totalField = await FieldAsync(orders, "total", "number");
        return (orders, new List<FieldDefinition> { customerField, totalField }, customers, customer);
    }

    [Fact]
    public async Task A_record_carries_self_collection_and_a_link_per_reference()
    {
        var (orders, fields, _, customer) = await ShopAsync();
        var order = await RecordAsync(orders, new JsonObject { ["customer"] = customer.Id, ["total"] = 10 });

        var relations = await ApiLinks.RelationsAsync(_db, fields);
        var extras = await ApiLinks.ForRecordAsync(_db, "orders", order, relations, Array.Empty<ApiLinks.Relation>(), "alice");

        Assert.Equal($"/api/v1/orders/records/{order.Id}", extras.Links["self"]!.GetValue<string>());
        Assert.Equal("/api/v1/orders/records", extras.Links["collection"]!.GetValue<string>());
        Assert.Equal($"/api/v1/customers/records/{customer.Id}", extras.Links["customer"]!.GetValue<string>());
        Assert.Null(extras.Expanded);
    }

    // A link the API would refuse is worse than no link: the target table is not published here.
    [Fact]
    public async Task An_unpublished_target_is_neither_linked_nor_expandable()
    {
        var (orders, fields, _, customer) = await ShopAsync(customersPublished: false);
        var order = await RecordAsync(orders, new JsonObject { ["customer"] = customer.Id });

        var relations = await ApiLinks.RelationsAsync(_db, fields);
        Assert.Empty(relations);

        var extras = await ApiLinks.ForRecordAsync(_db, "orders", order, relations, Array.Empty<ApiLinks.Relation>(), "alice");
        Assert.False(extras.Links.ContainsKey("customer"));

        var (_, error) = ApiLinks.ParseExpand("customer", relations);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task A_target_whose_GET_is_switched_off_is_not_a_relation()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var customers = await TableAsync("Customers", "customers", methods: "POST");
        var orders = await TableAsync("Orders", "orders");
        var customerField = await FieldAsync(orders, "customer", "reference", $$"""{ "tableId": "{{customers.Id}}" }""");

        Assert.Empty(await ApiLinks.RelationsAsync(_db, new[] { customerField }));
    }

    [Fact]
    public async Task Expand_embeds_the_referenced_record_one_level_deep()
    {
        var (orders, fields, _, customer) = await ShopAsync();
        var order = await RecordAsync(orders, new JsonObject { ["customer"] = customer.Id, ["total"] = 10 });

        var relations = await ApiLinks.RelationsAsync(_db, fields);
        var (expand, error) = ApiLinks.ParseExpand("customer", relations);
        Assert.Null(error);

        var extras = await ApiLinks.ForRecordAsync(_db, "orders", order, relations, expand, "alice");
        var embedded = extras.Expanded!["customer"]!.AsObject();
        Assert.Equal(customer.Id, embedded["id"]!.GetValue<string>());
        Assert.Equal("Acme", embedded["data"]!["name"]!.GetValue<string>());
        // One level: the embed is a plain record, not another set of links to follow.
        Assert.False(embedded.ContainsKey("links"));
    }

    // The whole point of the guard: $expand must not read what a direct GET of the target would refuse.
    [Fact]
    public async Task Expand_obeys_the_targets_read_rule()
    {
        var (orders, fields, _, customer) = await ShopAsync(customersReadRule: "_ROW_.owner = _USER_.id");
        var order = await RecordAsync(orders, new JsonObject { ["customer"] = customer.Id });

        var relations = await ApiLinks.RelationsAsync(_db, fields);
        var (expand, _) = ApiLinks.ParseExpand("customer", relations);

        var mine = await ApiLinks.ForRecordAsync(_db, "orders", order, relations, expand, "alice");
        Assert.NotNull(mine.Expanded!["customer"]);

        var theirs = await ApiLinks.ForRecordAsync(_db, "orders", order, relations, expand, "bob");
        Assert.Null(theirs.Expanded);
    }

    [Fact]
    public async Task A_misspelled_relation_is_refused_rather_than_ignored()
    {
        var (_, fields, _, _) = await ShopAsync();
        var relations = await ApiLinks.RelationsAsync(_db, fields);

        var (_, error) = ApiLinks.ParseExpand("custmer", relations);
        Assert.Contains("custmer", error);

        // A non-reference field is not expandable either.
        var (_, scalar) = ApiLinks.ParseExpand("total", relations);
        Assert.NotNull(scalar);
    }

    [Fact]
    public async Task A_field_named_self_cannot_shadow_the_self_link()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var customers = await TableAsync("Customers", "customers");
        var orders = await TableAsync("Orders", "orders");
        var field = await FieldAsync(orders, "self", "reference", $$"""{ "tableId": "{{customers.Id}}" }""");
        var customer = await RecordAsync(customers, new JsonObject());
        var order = await RecordAsync(orders, new JsonObject { ["self"] = customer.Id });

        var relations = await ApiLinks.RelationsAsync(_db, new[] { field });
        var extras = await ApiLinks.ForRecordAsync(_db, "orders", order, relations, Array.Empty<ApiLinks.Relation>(), "alice");

        Assert.Equal($"/api/v1/orders/records/{order.Id}", extras.Links["self"]!.GetValue<string>());
    }

    [Fact]
    public void Page_links_keep_the_callers_query_and_only_offer_pages_that_exist()
    {
        var request = new DefaultHttpContext().Request;
        request.Path = "/api/v1/orders/records";
        request.QueryString = new QueryString("?q=acme&page=2&$expand=customer");

        var links = ApiLinks.PageLinks(request, new QueryEngine.ListPage(Array.Empty<Record>(), 120, 2, 50, true));

        Assert.Equal("/api/v1/orders/records?q=acme&$expand=customer&page=1", links["first"]!.GetValue<string>());
        Assert.Equal("/api/v1/orders/records?q=acme&$expand=customer&page=1", links["prev"]!.GetValue<string>());
        Assert.Equal("/api/v1/orders/records?q=acme&$expand=customer&page=3", links["next"]!.GetValue<string>());
        Assert.Equal("/api/v1/orders/records?q=acme&$expand=customer&page=3", links["last"]!.GetValue<string>());
        Assert.Equal("/api/v1/orders/records?q=acme&$expand=customer&page=2", links["self"]!.GetValue<string>());
    }

    [Fact]
    public void The_first_page_offers_no_prev_and_a_last_page_no_next()
    {
        var request = new DefaultHttpContext().Request;
        request.Path = "/api/v1/orders/records";

        var links = ApiLinks.PageLinks(request, new QueryEngine.ListPage(Array.Empty<Record>(), 10, 1, 50, false));

        Assert.False(links.ContainsKey("prev"));
        Assert.False(links.ContainsKey("next"));
        Assert.Equal("/api/v1/orders/records?page=1", links["last"]!.GetValue<string>());
    }

    // Past the count ceiling the total is a floor, so a last link would point at the wrong page.
    [Fact]
    public void No_last_link_is_offered_once_the_count_stops_being_exact()
    {
        var request = new DefaultHttpContext().Request;
        request.Path = "/api/v1/orders/records";

        var links = ApiLinks.PageLinks(request, new QueryEngine.ListPage(Array.Empty<Record>(), QueryEngine.CountCeiling, 1, 50, true));

        Assert.False(links.ContainsKey("last"));
        Assert.True(links.ContainsKey("next"));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
