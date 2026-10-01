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
    public void DisabledMethodRefused()
    {
        var table = Table("GET,POST");

        Assert.True(ApiMethods.Allows(table, "GET"));
        Assert.True(ApiMethods.Allows(table, "POST"));
        Assert.False(ApiMethods.Allows(table, "DELETE"));
        Assert.False(ApiMethods.Allows(table, "PUT"));
    }

    [Fact]
    public void MethodIsCaseInsensitive()
    {
        Assert.True(ApiMethods.Allows(Table("get"), "GET"));
        Assert.True(ApiMethods.Allows(Table("GET"), "get"));
    }

    [Fact]
    public void UnknownMethodDropped()
    {

        Assert.Equal("GET,POST", ApiMethods.Serialize(new[] { "GET", "TRACE", "POST", "get" }));
        Assert.Equal(new[] { "GET" }, ApiMethods.Parse("GET,NONSENSE,,GET"));
    }

    [Fact]
    public void DefaultAllowsAll()
    {
        var fresh = new TableDefinition();

        Assert.Equal(ApiMethods.All, ApiMethods.Parse(fresh.ApiMethods));
    }

    [Fact]
    public void ReadOnlyKeyCannotWrite()
    {
        var table = Table("GET,POST,PATCH,PUT,DELETE");
        var readOnlyKey = new UserAccount { Id = "acct-1", ApiTokenMethods = "GET" };

        Assert.True(ApiMethods.Allows(table, readOnlyKey, "GET"));
        Assert.False(ApiMethods.Allows(table, readOnlyKey, "POST"));
        Assert.False(ApiMethods.Allows(table, readOnlyKey, "DELETE"));
    }

    [Fact]
    public void KeyCannotExceedTable()
    {
        var table = Table("GET,POST");
        var fullAccessKey = new UserAccount { Id = "acct-2" };

        Assert.True(ApiMethods.Allows(table, fullAccessKey, "POST"));
        Assert.False(ApiMethods.Allows(table, fullAccessKey, "DELETE"));
    }

    [Fact]
    public void NewAccountAllowsAllMethods()
    {
        var fresh = new UserAccount();

        Assert.Equal(ApiMethods.All, ApiMethods.Parse(fresh.ApiTokenMethods));
    }

    [Fact]
    public void PublishWithoutMethodsRefused()
    {
        var table = Table("");

        var errs = FieldValidation.ValidateTable(table, Array.Empty<string>());

        Assert.Contains(errs, e => e.Contains("HTTP method"));
    }

    [Fact]
    public void UnpublishedMayDisableAll()
    {
        var table = Table("");
        table.ApiEnabled = false;
        table.ApiName = "";

        Assert.Empty(FieldValidation.ValidateTable(table, Array.Empty<string>()));
    }

    [Fact]
    public void DocumentationIsBounded()
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
