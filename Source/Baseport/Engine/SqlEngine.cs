using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Baseport;

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

    public const int MaxRows = 200;

    public sealed record Result(List<string> Columns, List<List<string?>> Rows, bool Truncated, string? Error);

    private static void Pragma(System.Data.Common.DbConnection conn, string pragma)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA {pragma}";
        cmd.ExecuteNonQuery();
    }

    public static async Task<Result> ReadAsync(AppDbContext db, string sql, Action<SqliteConnection>? configure = null, bool restrict = true)
    {
        var owned = db.Database.GetDbConnection();
        var source = new SqliteConnectionStringBuilder(owned.ConnectionString);

        var inMemory = source.Mode == SqliteOpenMode.Memory || source.DataSource == ":memory:";
        var conn = inMemory
            ? owned
            : new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = source.DataSource,

                Mode = configure is null ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite
            }.ToString());

        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        try
        {
            if (!wasOpen) await conn.OpenAsync();
            if (configure is not null && conn is SqliteConnection sqlite)
            {

                if (!inMemory) Pragma(conn, "query_only = 0");
                configure(sqlite);

                if (!inMemory) Pragma(conn, "query_only = 1");
            }

            if (restrict && configure is not null && conn is SqliteConnection guarded) WireCatalog.Restrict(guarded);

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

            if (configure is not null && conn is SqliteConnection guarded) WireCatalog.Unrestrict(guarded);
            if (!wasOpen) await conn.CloseAsync();
            if (!inMemory) await conn.DisposeAsync();
        }
    }
}
