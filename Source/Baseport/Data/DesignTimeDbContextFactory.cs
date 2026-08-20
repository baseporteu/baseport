using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Baseport;

// Without this the ef tool builds the whole web host to find the context, which starts Serilog, the job scheduler and the listener.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        AppDbContext.Open("Data Source=baseport.db");
}
