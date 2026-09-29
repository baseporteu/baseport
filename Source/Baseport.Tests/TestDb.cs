using Baseport;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

internal static class TestDb
{
    public static AppDbContext Open(System.Data.Common.DbConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(options, connection);
        return new AppDbContext(options.Options);
    }

    public static AppDbContext Open(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(options, connectionString);
        return new AppDbContext(options.Options);
    }
}

internal static class TestSecrets
{
    private static readonly Lazy<bool> Configured = new(() =>
    {
        Secrets.Configure(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        return true;
    });

    public static void Ensure() => _ = Configured.Value;
}
