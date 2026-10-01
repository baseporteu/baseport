using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Baseport.Tests;

public sealed class ClientErrorEndpointsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly ServiceProvider _services;

    public ClientErrorEndpointsTests()
    {
        _connection.Open();
        _services = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseSqlite(_connection)).BuildServiceProvider();
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private async Task<List<AuditLog>> ReportAsync(UserAccount? caller)
    {
        var writer = new AuditLogWriter(_services.GetRequiredService<IServiceScopeFactory>());
        await writer.StartAsync(TestContext.Current.CancellationToken);
        ClientErrorEndpoints.Record(writer, new JsonObject { ["message"] = "boom", ["page"] = "/_/admin" }, caller);
        await writer.StopAsync(TestContext.Current.CancellationToken);

        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnonymousErrorNotAudited() =>
        Assert.Empty(await ReportAsync(null));

    [Theory]
    [InlineData(AccountRoles.User)]
    [InlineData(AccountRoles.Consumer)]
    public async Task NonAdminErrorNotAudited(string role) =>
        Assert.Empty(await ReportAsync(new UserAccount { Id = "acct00000001", Role = role }));

    [Fact]
    public async Task AdminErrorAuditedOnce()
    {
        var row = Assert.Single(await ReportAsync(new UserAccount { Id = "admin0000001", Role = AccountRoles.Admin }));

        Assert.Equal("admin0000001", row.UserId);
        Assert.Equal("boom", row.Message);
    }
}
