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

    private static readonly HashSet<string> IntrospectionPragmas = new(StringComparer.OrdinalIgnoreCase)
    {
        "table_info", "table_xinfo", "table_list", "index_list", "index_info", "index_xinfo",
        "foreign_key_list", "foreign_key_check", "integrity_check", "quick_check"
    };

    private static readonly HashSet<string> ReadablePragmas = new(StringComparer.OrdinalIgnoreCase)
    {
        "user_version", "schema_version", "application_id", "page_count", "page_size", "freelist_count",
        "journal_mode", "encoding", "database_list", "collation_list", "function_list", "pragma_list",
        "module_list", "compile_options", "data_version", "foreign_keys"
    };

    // console pragmas may only read
    private static readonly SQLitePCL.delegate_authorizer ReadOnlyPragmas = (_, action, name, argument, _, _) =>
    {
        if (action != SQLitePCL.raw.SQLITE_PRAGMA) return SQLitePCL.raw.SQLITE_OK;
        var pragma = name.utf8_to_string();
        var allowed = IntrospectionPragmas.Contains(pragma)
            || ReadablePragmas.Contains(pragma) && string.IsNullOrEmpty(argument.utf8_to_string());
        return allowed ? SQLitePCL.raw.SQLITE_OK : SQLitePCL.raw.SQLITE_DENY;
    };

    internal static TimeSpan StatementDeadline = TimeSpan.FromSeconds(10);

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
                Pooling = false,

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
            else SQLitePCL.raw.sqlite3_set_authorizer(((SqliteConnection)conn).Handle, ReadOnlyPragmas, null);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var deadline = StatementDeadline;
            SQLitePCL.raw.sqlite3_progress_handler(((SqliteConnection)conn).Handle, 1000, _ => clock.Elapsed > deadline ? 1 : 0, null);

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
        catch (SqliteException interrupted) when (interrupted.SqliteErrorCode == 9)
        {
            return new Result(new List<string>(), new List<List<string?>>(), false,
                $"The query ran longer than {StatementDeadline.TotalSeconds:0.#} seconds and was stopped.");
        }
        catch (SqliteException denied) when (denied.SqliteErrorCode == 23)
        {
            return new Result(new List<string>(), new List<List<string?>>(), false,
                "That statement is not allowed here. Queries can read, not change settings or data.");
        }
        catch (Exception ex)
        {
            return new Result(new List<string>(), new List<List<string?>>(), false, ex.Message);
        }
        finally
        {
            if (conn is SqliteConnection { State: System.Data.ConnectionState.Open } open)
                SQLitePCL.raw.sqlite3_progress_handler(open.Handle, 0, null, null);

            if (conn is SqliteConnection { State: System.Data.ConnectionState.Open } restricted) WireCatalog.Unrestrict(restricted);
            if (!wasOpen) await conn.CloseAsync();
            if (!inMemory) await conn.DisposeAsync();
        }
    }
}
