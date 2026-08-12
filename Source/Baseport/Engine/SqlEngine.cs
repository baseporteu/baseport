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

    // Runs a validated query and hands back the grid.
    public static async Task<Result> ReadAsync(AppDbContext db, string sql)
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
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());

        // Closing a connection the caller opened destroys an in-memory SQLite database.
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        try
        {
            if (!wasOpen) await conn.OpenAsync();
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
            if (!wasOpen) await conn.CloseAsync();
            if (!inMemory) await conn.DisposeAsync();
        }
    }
}
