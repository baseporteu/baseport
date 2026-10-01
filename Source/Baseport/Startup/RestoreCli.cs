namespace Baseport;

public static class RestoreCli
{
    public static async Task<int> RunAsync(string[] args, string bundledSettings, string localSettings)
    {
        var archive = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (archive is null)
        {
            Console.Error.WriteLine("Usage: baseport restore <archive> [--yes]");
            return 1;
        }
        var connectionString = ConfigCli.Build(bundledSettings, localSettings)["Baseport:ConnectionString"] ?? "Data Source=baseport.db";
        var source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        return await RunAsync(DataPaths.For(source), archive, args.Contains("--yes"), Console.In, Console.Out);
    }

    public static async Task<int> RunAsync(DataPaths data, string archive, bool yes, TextReader input, TextWriter output)
    {
        if (!InstanceLock.TryAcquire(data.Database, out var held))
        {
            output.WriteLine("Baseport is running on this database. Stop it first: baseport stop.");
            return 1;
        }

        using (held)
        {
            var (plan, problem) = await BackupRestore.StageAsync(archive, data);
            if (plan is null)
            {
                output.WriteLine($"Restore refused: {problem}");
                return 1;
            }

            output.WriteLine($"Restoring {Path.GetFullPath(archive)} ({BackupStore.Human(plan.Bytes)}).");
            foreach (var (path, bytes) in BackupRestore.Replaces(plan, data))
                output.WriteLine($"  replaces {path} ({BackupStore.Human(bytes)})");
            if (!yes)
            {
                output.Write("The current files are moved aside, not deleted. Continue? [y/N] ");
                if (input.ReadLine()?.Trim().ToLowerInvariant() is not ("y" or "yes"))
                {
                    BackupRestore.Discard(plan);
                    output.WriteLine("Cancelled, nothing was changed.");
                    return 1;
                }
            }

            var aside = BackupRestore.Apply(plan, data);
            output.WriteLine($"Restored. The previous files are in {aside}.");
            if (plan.DatabaseOnly)
                output.WriteLine("This was a database-only snapshot: uploads and keys were left as they were.");
            else if (!plan.SigningKey)
                output.WriteLine("No signing key in this archive: every user signs in again after start.");
            output.WriteLine("Start Baseport to apply any pending migrations.");
            return 0;
        }
    }
}
