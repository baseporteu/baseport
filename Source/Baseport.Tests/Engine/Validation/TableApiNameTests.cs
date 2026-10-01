using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class TableApiNameTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public TableApiNameTests()
    {
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

    private static TableDefinition Table(string apiName, bool published) =>
        new() { Id = Ids.NewShortId(12), Name = "Orders", ApiName = apiName, ApiEnabled = published };

    [Fact]
    public void PublishWithoutApiNameRejected()
    {
        var errs = FieldValidation.ValidateTable(Table("", true), Array.Empty<string>());
        Assert.Contains(errs, e => e.Contains("API name"));
    }

    [Fact]
    public void ClearApiNameWhenUnpublished()
    {
        Assert.Empty(FieldValidation.ValidateTable(Table("", false), Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Sales Orders")]
    [InlineData("sales_orders")]
    [InlineData("1orders")]
    [InlineData("o")]
    [InlineData("orders/records")]
    public void ApiNamePatternEnforced(string apiName)
    {
        Assert.NotEmpty(FieldValidation.ValidateTable(Table(apiName, true), Array.Empty<string>()));
    }

    [Fact]
    public void ApiNameNormalized()
    {
        var table = Table("  Sales-Orders  ", true);
        Assert.Empty(FieldValidation.ValidateTable(table, Array.Empty<string>()));
        Assert.Equal("sales-orders", table.ApiName);
    }

    [Fact]
    public void ReservedApiNameRejected()
    {
        Assert.NotEmpty(FieldValidation.ValidateTable(Table("openapi", true), Array.Empty<string>()));
    }

    [Fact]
    public async Task RejectedEditNotSaved()
    {
        var table = Table("sales-orders", true);
        _db.Tables.Add(table);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        table.ApiName = "";
        Assert.NotEmpty(FieldValidation.ValidateTable(table, Array.Empty<string>()));

        await using var audit = TestDb.Open(_connection);
        audit.AuditLogs.Add(new AuditLog
        {
            Id = Ids.NewShortId(12),
            CreatedAt = DateTime.UtcNow,
            Method = "PATCH",
            Path = "/api/_admin/tables/x",
            Status = 400
        });
        await audit.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await audit.Tables.AsNoTracking().FirstAsync(t => t.Id == table.Id, TestContext.Current.CancellationToken);
        Assert.Equal("sales-orders", stored.ApiName);
    }
}
