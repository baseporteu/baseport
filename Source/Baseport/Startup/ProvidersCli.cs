using System.Net;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

// `baseport providers ...`: edits the same AppSettings row the admin ui's providers pane does, so a running server's PostgresServer/TdsServer pick up the change on their next poll.
public static class ProvidersCli
{
    public static async Task<int> RunAsync(string[] args, string bundledSettings, string localSettings)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(bundledSettings, optional: true)
            .AddJsonFile(localSettings, optional: true)
            .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = config["Baseport:ConnectionString"] ?? "Data Source=baseport.db";

        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options);

        try
        {
            await db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open the database at \"{connectionString}\": {ex.Message}");
            return 1;
        }

        var rest = args.Skip(1).ToArray();
        if (rest is ["status"]) return await PrintStatusAsync(db);
        if (rest is [("postgres" or "tds") and var provider, ("enable" or "disable") and var action, ..])
            return await SetAsync(db, provider, action, rest[2..]);

        PrintUsage();
        return rest.Length == 0 ? 0 : 1;
    }

    private static async Task<int> PrintStatusAsync(AppDbContext db)
    {
        var s = await db.SettingsAsync() ?? new AppSettings();
        Console.WriteLine($"postgres  {(s.PostgresEnabled ? "enabled " : "disabled")}  {s.PostgresBindAddress}:{s.PostgresPort}");
        Console.WriteLine($"tds       {(s.TdsEnabled ? "enabled " : "disabled")}  {s.TdsBindAddress}:{s.TdsPort}");
        return 0;
    }

    private static async Task<int> SetAsync(AppDbContext db, string provider, string action, string[] rest)
    {
        int? port = null;
        string? bind = null;
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--port" && i + 1 < rest.Length && int.TryParse(rest[++i], out var p)) port = p;
            else if (rest[i] == "--bind" && i + 1 < rest.Length) bind = rest[++i];
        }
        if (port is { } portValue && (portValue < 1 || portValue > 65535))
        {
            Console.Error.WriteLine("Port must be between 1 and 65535.");
            return 1;
        }
        if (bind is not null && !IPAddress.TryParse(bind, out _))
        {
            Console.Error.WriteLine("Bind address must be a valid IP address.");
            return 1;
        }

        var s = await db.SettingsAsync();
        if (s is null) { s = new AppSettings(); db.AppSettings.Add(s); }

        var enabled = action == "enable";
        if (provider == "postgres")
        {
            s.PostgresEnabled = enabled;
            if (port is { } pp) s.PostgresPort = pp;
            if (bind is not null) s.PostgresBindAddress = bind;
        }
        else
        {
            s.TdsEnabled = enabled;
            if (port is { } tp) s.TdsPort = tp;
            if (bind is not null) s.TdsBindAddress = bind;
        }

        await db.SaveChangesAsync();
        Console.WriteLine($"{provider} {(enabled ? "enabled" : "disabled")}. A running server picks this up within a few seconds.");
        return 0;
    }

    private static void PrintUsage() => Console.WriteLine("""
        Usage:
          baseport providers status
          baseport providers postgres enable [--port N] [--bind ADDR]
          baseport providers postgres disable
          baseport providers tds enable [--port N] [--bind ADDR]
          baseport providers tds disable
        """);
}
