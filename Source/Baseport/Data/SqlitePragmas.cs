using System.Data.Common;
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

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Statements;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Statements;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
