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
