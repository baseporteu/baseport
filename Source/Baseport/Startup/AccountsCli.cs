using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class AccountsCli
{

    public static string Invocation()
    {
        var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe)) return "baseport";
        if (dll.Length == 0 || string.Equals(Path.GetFileNameWithoutExtension(exe), Path.GetFileNameWithoutExtension(dll), StringComparison.OrdinalIgnoreCase))
            return Quote(exe);
        return $"dotnet {Quote(dll)}";
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    internal static bool MissingDatabase(string connectionString)
    {
        string source;
        try
        {
            source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (source.Length == 0 || source.Contains(":memory:", StringComparison.Ordinal)) return false;
        if (File.Exists(source)) return false;

        Console.Error.WriteLine($"No Baseport database at \"{Path.GetFullPath(source)}\".");
        Console.Error.WriteLine("Run this from the directory Baseport runs in, or point it at the file:");
        Console.Error.WriteLine($"  Baseport__ConnectionString=\"Data Source=/path/to/baseport.db\" {Invocation()} ...");
        return true;
    }

    public static async Task<int> RunAsync(string[] args, string bundledSettings, string localSettings)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(bundledSettings, optional: true)
            .AddJsonFile(localSettings, optional: true)
            .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = config["Baseport:ConnectionString"] ?? "Data Source=baseport.db";

        if (MissingDatabase(connectionString)) return 1;

        using var db = AppDbContext.Open(connectionString);

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
        if (rest is ["rename", var from, var to]) return await RenameAsync(db, from, to);
        if (rest is ["link", var linkUser, var slug, var subject]) return await LinkAsync(db, linkUser, slug, subject);
        if (rest is ["unlink", var unlinkUser]) return await UnlinkAsync(db, unlinkUser);

        PrintUsage();
        return rest.Length == 0 ? 0 : 1;
    }

    private static async Task<int> ListAsync(AppDbContext db)
    {
        var accounts = await db.UserAccounts.OrderBy(a => a.Username).ToListAsync();
        var providers = await db.OidcProviders.ToDictionaryAsync(p => p.Id, p => p.Slug);
        Console.WriteLine($"{"USERNAME",-24} {"ROLE",-10} {"STATE",-10} {"SIGN-IN",-16}");
        foreach (var a in accounts)
        {

            var ways = new List<string>();
            if (a.PasswordHash.Length > 0) ways.Add("password");
            if (a.OidcSubject.Length > 0) ways.Add(providers.GetValueOrDefault(a.OidcProviderId, "sso"));
            Console.WriteLine($"{a.Username,-24} {a.Role,-10} {(a.IsDisabled ? "disabled" : "enabled"),-10} {(ways.Count > 0 ? string.Join("+", ways) : "none"),-16}");
        }
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

        if (role != AccountRoles.Admin && await AdminEndpoints.IsLastEnabledAdmin(db, account))
        {
            Console.Error.WriteLine($"{account.Username} is the last enabled admin. Promote another account first.");
            return 1;
        }

        account.Role = role;
        account.UpdatedAt = DateTime.UtcNow;

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

    private static async Task<int> RenameAsync(AppDbContext db, string username, string next)
    {
        if (await FindAsync(db, username) is not { } account) return 1;

        if (account.Username == next)
        {
            Console.WriteLine($"{account.Username} already has that name.");
            return 0;
        }
        if (AccountValidation.Validate(next, account.Email) is { Count: > 0 } errors)
        {
            foreach (var error in errors) Console.Error.WriteLine(error);
            return 1;
        }
        if (await db.UserAccounts.AnyAsync(a => a.Username == next && a.Id != account.Id))
        {
            Console.Error.WriteLine($"\"{next}\" is already taken.");
            return 1;
        }

        var was = account.Username;
        account.Username = next;
        account.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        Console.WriteLine($"{was} is now {next}.");
        return 0;
    }

    private static async Task<int> LinkAsync(AppDbContext db, string username, string slug, string subject)
    {
        if (await FindAsync(db, username) is not { } account) return 1;

        var provider = await db.OidcProviders.FirstOrDefaultAsync(p => p.Slug == slug);
        if (provider is null)
        {
            Console.Error.WriteLine($"No provider with the key \"{slug}\". Add it under Settings > Authentication > Single sign-on.");
            return 1;
        }

        subject = subject.Trim();
        if (subject.Length == 0)
        {
            Console.Error.WriteLine("A subject is required. It is printed by the sign-in that was refused.");
            return 1;
        }

        var taken = await db.UserAccounts.FirstOrDefaultAsync(a =>
            a.OidcProviderId == provider.Id && a.OidcSubject == subject && a.Id != account.Id);
        if (taken is not null)
        {
            Console.Error.WriteLine($"That identity is already linked to {taken.Username}. Unlink it first.");
            return 1;
        }

        account.OidcProviderId = provider.Id;
        account.OidcSubject = subject;
        account.UpdatedAt = DateTime.UtcNow;

        await UserTokens.RevokeAllAsync(db, account.Id);
        await db.SaveChangesAsync();

        Console.WriteLine($"{account.Username} now signs in through {provider.Name}. Every existing session is revoked.");
        return 0;
    }

    private static async Task<int> UnlinkAsync(AppDbContext db, string username)
    {
        if (await FindAsync(db, username) is not { } account) return 1;

        if (account.OidcSubject.Length == 0)
        {
            Console.WriteLine($"{account.Username} is not linked to a provider.");
            return 0;
        }

        if (account.PasswordHash.Length == 0)
        {
            Console.Error.WriteLine($"{account.Username} has no password, unlinking would leave no way to sign in. " +
                $"Set one first: {Invocation()} accounts password {account.Username} <password>");
            return 1;
        }

        account.OidcProviderId = "";
        account.OidcSubject = "";
        account.UpdatedAt = DateTime.UtcNow;
        await UserTokens.RevokeAllAsync(db, account.Id);
        await db.SaveChangesAsync();

        Console.WriteLine($"{account.Username} no longer signs in through a provider. Every existing session is revoked.");
        return 0;
    }

    private static async Task<UserAccount?> FindAsync(AppDbContext db, string handle)
    {
        var account = await db.UserAccounts.FirstOrDefaultAsync(a => a.Username == handle);
        if (account is not null) return account;

        var byEmail = await db.UserAccounts.Where(a => a.Email == handle && a.Email != "").Take(2).ToListAsync();
        if (byEmail.Count > 1)
        {
            Console.Error.WriteLine($"\"{handle}\" is the e-mail of more than one account. Name the account instead; {Invocation()} accounts list shows them.");
            return null;
        }
        if (byEmail.Count == 1) return byEmail[0];

        Console.Error.WriteLine($"No account with the username or e-mail \"{handle}\".");
        return null;
    }

    private static void PrintUsage() => CliHelp.List("accounts commands", new[]
    {
        "list",
        "promote <account>",
        "demote <account>",
        "password <account> <pw>",
        "rename <account> <new>",
        "link <account> <key> <subject>",
        "unlink <account>"
    });
}
