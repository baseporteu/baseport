using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// The published name is the whole public contract, so the API is the thing that decides whether one is acceptable.
public class TableApiNameTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public TableApiNameTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
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
    public void Publishing_without_an_api_name_is_rejected()
    {
        var errs = FieldValidation.ValidateTable(Table("", true), Array.Empty<string>());
        Assert.Contains(errs, e => e.Contains("API name"));
    }

    [Fact]
    public void Clearing_the_api_name_is_allowed_while_unpublished()
    {
        Assert.Empty(FieldValidation.ValidateTable(Table("", false), Array.Empty<string>()));
    }

    [Theory]
    [InlineData("Sales Orders")]
    [InlineData("sales_orders")]
    [InlineData("1orders")]
    [InlineData("o")]
    [InlineData("orders/records")]
    public void An_api_name_outside_the_pattern_is_rejected(string apiName)
    {
        Assert.NotEmpty(FieldValidation.ValidateTable(Table(apiName, true), Array.Empty<string>()));
    }

    [Fact]
    public void An_api_name_is_stored_lowercased_and_trimmed()
    {
        var table = Table("  Sales-Orders  ", true);
        Assert.Empty(FieldValidation.ValidateTable(table, Array.Empty<string>()));
        Assert.Equal("sales-orders", table.ApiName);
    }

    [Fact]
    public void A_reserved_api_name_is_rejected()
    {
        Assert.NotEmpty(FieldValidation.ValidateTable(Table("openapi", true), Array.Empty<string>()));
    }

    // The bug this pins: an endpoint mutates its tracked entity, validation fails, it returns 400 without saving, and then something later in the same request saves.
    [Fact]
    public async Task A_rejected_edit_is_not_written_by_a_later_save()
    {
        var table = Table("sales-orders", true);
        _db.Tables.Add(table);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The endpoint's context: applies the edit, then rejects it.
        table.ApiName = "";
        Assert.NotEmpty(FieldValidation.ValidateTable(table, Array.Empty<string>()));

        // The audit log's context, from its own scope over the same database.
        await using var audit = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
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
