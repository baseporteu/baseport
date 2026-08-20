using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Baseport;

// Shared read-only SQL validation/execution for the console and saved queries.
public static class SqlEngine
{
    private static readonly Regex Allowed = new(@"^\s*(SELECT|PRAGMA|EXPLAIN|WITH|VALUES)\b", RegexOptions.IgnoreCase);

    public static string? Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "Enter a query.";
        if (!Allowed.IsMatch(sql.TrimStart()))
            return "Only read-only queries (SELECT / PRAGMA / EXPLAIN / WITH) are allowed.";
        if (sql.TrimEnd().TrimEnd(';').Contains(';'))
            return "Only a single statement is allowed.";
        return null;
    }

    // At most this many rows come back; the caller reports the cut.
    public const int MaxRows = 200;

    public sealed record Result(List<string> Columns, List<List<string?>> Rows, bool Truncated, string? Error);

    private static void Pragma(System.Data.Common.DbConnection conn, string pragma)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma}";
        cmd.ExecuteNonQuery();
    }

    // Runs a validated query and hands back the grid. configure runs against the connection before the query, for dialect compatibility functions a wire provider needs.
    public static async Task<Result> ReadAsync(AppDbContext db, string sql, Action<SqliteConnection>? configure = null)
    {
        var owned = db.Database.GetDbConnection();
        var source = new SqliteConnectionStringBuilder(owned.ConnectionString);

        // an in-memory db lives only inside the connection that made it, so a read-only one would open an empty database instead
        var inMemory = source.Mode == SqliteOpenMode.Memory || source.DataSource == ":memory:";
        var conn = inMemory
            ? owned
            : new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = source.DataSource,
                // configure builds temp views and attached catalog schemas, and sqlite opens attached databases with the main database's flags, so a read-only handle cannot write even to :memory:. The statement itself is locked down with query_only below, which covers main, temp and attached alike.
                Mode = configure is null ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite
            }.ToString());

        // Closing a connection the caller opened destroys an in-memory SQLite database.
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        try
        {
            if (!wasOpen) await conn.OpenAsync();
            if (configure is not null && conn is SqliteConnection sqlite)
            {
                // sqlite connections are pooled, and query_only rides along on a pooled handle, so the lockdown from the last statement has to be lifted before this one can build its catalog
                if (!inMemory) Pragma(conn, "query_only = 0");
                configure(sqlite);
                // the owned connection belongs to the app and must stay writable; the ones opened here are ours to lock down before the caller's statement runs
                if (!inMemory) Pragma(conn, "query_only = 1");
            }
            // The wire providers (configure is not null) run an untrusted, api-token-authenticated statement, so it is confined to the projected catalog: no direct read of the system tables or the raw record store. The admin console and saved queries pass no configure and stay unrestricted.
            if (configure is not null && conn is SqliteConnection guarded) WireCatalog.Restrict(guarded);
            
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql.TrimEnd().TrimEnd(';');
            using var reader = await cmd.ExecuteReaderAsync();
            var columns = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
            var rows = new List<List<string?>>();
            while (await reader.ReadAsync() && rows.Count < MaxRows)
            {
                var row = new List<string?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    row.Add(reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
                rows.Add(row);
            }
            return new Result(columns, rows, rows.Count == MaxRows, null);
        }
        catch (Exception ex)
        {
            return new Result(new List<string>(), new List<List<string?>>(), false, ex.Message);
        }
        finally
        {
            // Clear the authorizer before the handle returns to the pool (or, for an in-memory db, before the owned connection is reused by the app), so the restriction never rides along on the next borrow.
            if (configure is not null && conn is SqliteConnection guarded) WireCatalog.Unrestrict(guarded);
            if (!wasOpen) await conn.CloseAsync();
            if (!inMemory) await conn.DisposeAsync();
        }
    }
}
