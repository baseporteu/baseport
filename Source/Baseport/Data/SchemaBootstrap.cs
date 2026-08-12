using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class SchemaBootstrap
{
    public static async Task ApplyAsync(AppDbContext db)
    {
        await MigrateAsync(db);
        await EnableWalAsync(db);

        if (!await db.UserAccounts.AnyAsync(u => u.Role == AccountRoles.Admin))
        {
            db.UserAccounts.Add(new UserAccount
            {
                Id = Ids.NewShortId(12),
                Username = "admin",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Role = AccountRoles.Admin,
                // Tokens are issued on-demand via Auth UI, not during seeding.
                ApiTokenHash = "",
                ApiEnabled = false
            });
            await db.SaveChangesAsync();
        }

        var settings = await db.SettingsAsync();
        if (settings is null)
        {
            settings = new AppSettings();
            db.AppSettings.Add(settings);
        }
        if (string.IsNullOrEmpty(settings.PreviewSecret))
            settings.PreviewSecret = Ids.NewShortId(48);
        await db.SaveChangesAsync();

        EmbedOrigins.Set(settings.AllowedOrigins);

        // Guarantees all dynamic table indexes exist.
        foreach (var table in await db.Tables.Include(t => t.Fields).ToListAsync())
            await RecordIndexes.SyncAsync(db, table);

        // Seed missing system job defaults and schedule initial runs.
        var now = DateTime.UtcNow;
        var addedJobs = false;
        foreach (var def in Jobs.All)
        {
            if (await db.JobConfigs.AnyAsync(j => j.Key == def.Key)) continue;
            db.JobConfigs.Add(new JobConfig
            {
                Key = def.Key,
                Name = def.Name,
                Schedule = def.DefaultSchedule,
                Enabled = def.DefaultEnabled,
                NextRunAt = Jobs.NextRun(def.DefaultSchedule, now) ?? now.AddHours(1)
            });
            addedJobs = true;
        }
        if (addedJobs) await db.SaveChangesAsync();
    }

    // Enables WAL mode for concurrent reads/writes.
    private static async Task EnableWalAsync(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State == System.Data.ConnectionState.Closed;
        if (wasClosed) await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL";
            await cmd.ExecuteScalarAsync();
        }
        finally
        {
            // Only close connection if opened locally (prevents destroying in-memory DBs).
            if (wasClosed) await conn.CloseAsync();
        }
    }


    // Applies any migration the file has not seen. A fresh file gets the whole schema; an existing one only what is new.
    private static async Task MigrateAsync(AppDbContext db)
    {
        // A database built by EnsureCreated has the tables but no history row, so Migrate would try to create what is already there.
        if (!(await db.Database.GetAppliedMigrationsAsync()).Any() && await HasTablesAsync(db))
            throw new InvalidOperationException(
                "This database predates migrations and cannot be upgraded in place. " +
                "Delete the database file and restart to rebuild it.");

        await db.Database.MigrateAsync();
    }

    private static async Task<bool> HasTablesAsync(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State == System.Data.ConnectionState.Closed;
        if (wasClosed) await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '_tables'";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            // Closing a connection the caller opened destroys an in-memory database.
            if (wasClosed) await conn.CloseAsync();
        }
    }
}
