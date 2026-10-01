using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;

namespace Baseport.Tests;

public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task ReadyNeedsMigrations()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        await using var db = TestDb.Open(conn);

        Assert.False(await HealthEndpoints.ReadyAsync(db, TestContext.Current.CancellationToken));

        await SchemaBootstrap.ApplyAsync(db);
        Assert.True(await HealthEndpoints.ReadyAsync(db, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProbesAreAnonymous()
    {
        Assert.True(AdminAuthMiddleware.IsPublicPath(HealthEndpoints.Live));
        Assert.True(AdminAuthMiddleware.IsPublicPath(HealthEndpoints.Ready));
        Assert.False(AdminSurface.IsAdminPath(HealthEndpoints.Ready));
    }
}
