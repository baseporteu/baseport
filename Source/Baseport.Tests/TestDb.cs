using Baseport;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

// Every test context goes through AppDbContext.Configure, the same call the host makes. Options built by hand here compile and run and quietly drop RecordChangeInterceptor, so UpdatedAt is never stamped: a concurrency-token test then passes against a version that never changes, and a subscription test hears nothing. The bug the tests exist to catch is the one they would be blind to.
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
