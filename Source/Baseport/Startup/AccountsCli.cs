using Microsoft.EntityFrameworkCore;

namespace Baseport;

// `baseport accounts ...`: the three operations the console deliberately refuses on an admin account. Whoever has the shell outranks whoever merely has console access, which is the same split TrailBase draws (crates/core/src/auth/cli.rs).
public static class AccountsCli
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
        if (rest is ["list"]) return await ListAsync(db);
        if (rest is ["promote", var promote]) return await SetRoleAsync(db, promote, AccountRoles.Admin);
        if (rest is ["demote", var demote]) return await SetRoleAsync(db, demote, AccountRoles.Consumer);
        if (rest is ["password", var target, var password]) return await SetPasswordAsync(db, target, password);

        PrintUsage();
        return rest.Length == 0 ? 0 : 1;
    }

    private static async Task<int> ListAsync(AppDbContext db)
    {
        var accounts = await db.UserAccounts.OrderBy(a => a.Username).ToListAsync();
        Console.WriteLine($"{"USERNAME",-24} {"ROLE",-10} {"STATE",-10}");
        foreach (var a in accounts)
            Console.WriteLine($"{a.Username,-24} {a.Role,-10} {(a.IsDisabled ? "disabled" : "enabled"),-10}");
        return 0;
    }

    private static async Task<int> SetRoleAsync(AppDbContext db, string username, string role)
    {
        if (await FindAsync(db, username) is not { } account) return 1;

        if (account.Role == role)
        {
            Console.WriteLine($"{account.Username} is already {role}.");
            return 0;
        }

        // The console cannot demote at all, so the last admin has to be protected here or there is no way back in.
        if (role != AccountRoles.Admin && await AdminEndpoints.IsLastEnabledAdmin(db, account))
        {
            Console.Error.WriteLine($"{account.Username} is the last enabled admin. Promote another account first.");
            return 1;
        }

        account.Role = role;
        account.UpdatedAt = DateTime.UtcNow;
        // A demoted operator must not keep the console session the old role opened.
        await UserTokens.RevokeAllAsync(db, account.Id);
        await db.SaveChangesAsync();

        Console.WriteLine($"{account.Username} is now {role}.");
        return 0;
    }

    private static async Task<int> SetPasswordAsync(AppDbContext db, string username, string password)
    {
        if (await FindAsync(db, username) is not { } account) return 1;

        if (AccountValidation.PasswordProblem(password) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 1;
        }

        account.PasswordHash = AdminAuth.HashPassword(password);
        account.MustChangePassword = true;
        account.UpdatedAt = DateTime.UtcNow;
        await UserTokens.RevokeAllAsync(db, account.Id);
        await db.SaveChangesAsync();

        Console.WriteLine($"Password set for {account.Username}. They must change it at the next sign-in, and every existing session is revoked.");
        return 0;
    }

    private static async Task<UserAccount?> FindAsync(AppDbContext db, string username)
    {
        var account = await db.UserAccounts.FirstOrDefaultAsync(a => a.Username == username);
        if (account is null) Console.Error.WriteLine($"No account named \"{username}\".");
        return account;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Usage: baseport accounts <command>

              list                        Show every account with its role and state.
              promote <username>          Grant console access.
              demote <username>           Remove console access.
              password <username> <pw>    Set a one-time password; the owner must change it.

            The console refuses all three on an admin account, deliberately: console
            access alone must not be enough to take over another operator's account.
            """);
    }
}
