using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public readonly record struct IntegrityResult(bool Ok, string Detail);

public static class DatabaseIntegrity
{
    public static async Task<IntegrityResult> CheckAsync(DbConnection conn, bool full = false, CancellationToken ct = default)
    {
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = full ? "PRAGMA integrity_check" : "PRAGMA quick_check";
            var lines = new List<string>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    lines.AddRange(reader.GetString(0).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(l => !l.StartsWith("***", StringComparison.Ordinal)));
            return lines is ["ok"] ? new(true, "ok") : new(false, lines.FirstOrDefault() ?? "no result");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            return new(false, ex.Message);
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    public static async Task<IntegrityResult> CheckFileAsync(string databaseFile, bool full = false, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        return await CheckAsync(conn, full, ct);
    }

    public static async Task EnsureHealthyAsync(AppDbContext db)
    {
        var clock = Stopwatch.StartNew();
        var result = await CheckAsync(db.Database.GetDbConnection());
        if (clock.Elapsed > TimeSpan.FromSeconds(10))
            Serilog.Log.Information("Database quick_check took {Seconds:F0} s", clock.Elapsed.TotalSeconds);
        if (!result.Ok) throw new DatabaseCorruptException(db.Database.GetDbConnection().DataSource, result.Detail);
    }

    public static async Task<int> RunCliAsync(string connectionString, TextWriter output)
    {
        var source = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (!File.Exists(source))
        {
            output.WriteLine($"No database at {Path.GetFullPath(source)}.");
            return 1;
        }
        var result = await CheckFileAsync(source);
        output.WriteLine(result.Ok ? $"ok {Path.GetFullPath(source)}" : $"damaged {Path.GetFullPath(source)}: {result.Detail}");
        return result.Ok ? 0 : 1;
    }
}

public sealed class DatabaseCorruptException(string database, string detail)
    : Exception($"The database at {database} is damaged ({detail}).")
{
    public string Database { get; } = database;

    public string Detail { get; } = detail;
}
