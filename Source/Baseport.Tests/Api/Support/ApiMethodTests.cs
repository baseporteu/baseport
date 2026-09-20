using Xunit;
using Baseport;

namespace Baseport.Tests;

public class ApiMethodTests
{
    private static TableDefinition Table(string methods) => new()
    {
        Id = Ids.NewShortId(12),
        Name = "Orders",
        ApiName = "sales-orders",
        ApiEnabled = true,
        ApiMethods = methods
    };

    [Fact]
    public void A_disabled_method_is_refused()
    {
        var table = Table("GET,POST");

        Assert.True(ApiMethods.Allows(table, "GET"));
        Assert.True(ApiMethods.Allows(table, "POST"));
        Assert.False(ApiMethods.Allows(table, "DELETE"));
        Assert.False(ApiMethods.Allows(table, "PUT"));
    }

    [Fact]
    public void The_method_is_matched_whatever_case_it_arrives_in()
    {
        Assert.True(ApiMethods.Allows(Table("get"), "GET"));
        Assert.True(ApiMethods.Allows(Table("GET"), "get"));
    }

    [Fact]
    public void An_unknown_method_is_dropped_rather_than_stored()
    {

        Assert.Equal("GET,POST", ApiMethods.Serialize(new[] { "GET", "TRACE", "POST", "get" }));
        Assert.Equal(new[] { "GET" }, ApiMethods.Parse("GET,NONSENSE,,GET"));
    }

    [Fact]
    public void The_default_answers_everything()
    {
        var fresh = new TableDefinition();

        Assert.Equal(ApiMethods.All, ApiMethods.Parse(fresh.ApiMethods));
    }

    [Fact]
    public void A_key_scoped_to_read_only_cannot_write_even_when_the_table_allows_it()
    {
        var table = Table("GET,POST,PATCH,PUT,DELETE");
        var readOnlyKey = new UserAccount { Id = "acct-1", ApiTokenMethods = "GET" };

        Assert.True(ApiMethods.Allows(table, readOnlyKey, "GET"));
        Assert.False(ApiMethods.Allows(table, readOnlyKey, "POST"));
        Assert.False(ApiMethods.Allows(table, readOnlyKey, "DELETE"));
    }

    [Fact]
    public void A_full_access_key_still_cannot_exceed_what_the_table_publishes()
    {
        var table = Table("GET,POST");
        var fullAccessKey = new UserAccount { Id = "acct-2" };

        Assert.True(ApiMethods.Allows(table, fullAccessKey, "POST"));
        Assert.False(ApiMethods.Allows(table, fullAccessKey, "DELETE"));
    }

    [Fact]
    public void A_freshly_issued_account_defaults_to_every_method_so_existing_keys_are_unaffected()
    {
        var fresh = new UserAccount();

        Assert.Equal(ApiMethods.All, ApiMethods.Parse(fresh.ApiTokenMethods));
    }

    [Fact]
    public void Publishing_a_table_that_answers_nothing_is_rejected()
    {
        var table = Table("");

        var errs = FieldValidation.ValidateTable(table, Array.Empty<string>());

        Assert.Contains(errs, e => e.Contains("HTTP method"));
    }

    [Fact]
    public void An_unpublished_table_may_have_every_method_off()
    {
        var table = Table("");
        table.ApiEnabled = false;
        table.ApiName = "";

        Assert.Empty(FieldValidation.ValidateTable(table, Array.Empty<string>()));
    }

    [Fact]
    public void Documentation_fields_are_bounded_because_they_are_published()
    {
        var table = Table("GET");
        table.ApiDisplayName = new string('x', 65);
        table.ApiNamespace = new string('y', 65);
        table.ApiDocumentation = new string('z', 8001);

        var errs = FieldValidation.ValidateTable(table, Array.Empty<string>());

        Assert.Contains(errs, e => e.Contains("documentation name"));
        Assert.Contains(errs, e => e.Contains("namespace"));
        Assert.Contains(errs, e => e.Contains("documentation is too long"));
    }
}
