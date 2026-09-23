using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Baseport;

public sealed class SqlitePragmas : DbConnectionInterceptor
{
    private const string Statements = """
        PRAGMA synchronous=NORMAL;
        PRAGMA busy_timeout=5000;
        PRAGMA mmap_size=268435456;
        PRAGMA temp_store=MEMORY;
        PRAGMA cache_size=-32000;
        """;

    internal static bool Regexp(string? pattern, string? input)
    {
        if (pattern is null || input is null) return false;
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
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
