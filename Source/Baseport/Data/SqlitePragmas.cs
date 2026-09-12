using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Baseport;

// SQLite's defaults are a desktop file's, not a server's: FULL is an fsync per write, and without busy_timeout a second writer gets SQLITE_BUSY immediately.
public sealed class SqlitePragmas : DbConnectionInterceptor
{
    private const string Statements = """
        PRAGMA synchronous=NORMAL;
        PRAGMA busy_timeout=5000;
        PRAGMA mmap_size=268435456;
        PRAGMA temp_store=MEMORY;
        PRAGMA cache_size=-32000;
        """;

    // a search box's /pattern/ ends up here; time-boxed so a catastrophic-backtracking pattern can't hang the connection
    private static bool Regexp(string? pattern, string? input)
    {
        if (pattern is null || input is null) return false;
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(50));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RegisterFunctions(DbConnection connection)
    {
        if (connection is SqliteConnection sqlite) sqlite.CreateFunction<string?, string?, bool>("regexp", Regexp);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        RegisterFunctions(connection);
        using var command = connection.CreateCommand();
        command.CommandText = Statements;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        RegisterFunctions(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = Statements;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
