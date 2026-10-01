using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Baseport;

public enum SqlSourceKind { Sqlite, SqlServer, Postgres }

[JsonConverter(typeof(JsonStringEnumConverter<ColumnChoice>))]
public enum ColumnChoice
{
    [JsonStringEnumMemberName("map")] Map,
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("skip")] Skip
}

public static class SqlSource
{
    public const int PageSize = 500;

    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public sealed record SourceTable(string Schema, string Name)
    {
        public string Display => Schema.Length == 0 ? Name : $"{Schema}.{Name}";
    }

    public sealed record SourceColumn(string Name, string SourceType, string? FieldType, bool IsKey);

    public sealed record ColumnSetting(string Column, ColumnChoice Choice);

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SqlSourceKind Kind(Connection c) => c.Protocol switch
    {
        ConnectionProtocols.Sqlite => SqlSourceKind.Sqlite,
        ConnectionProtocols.SqlServer => SqlSourceKind.SqlServer,
        ConnectionProtocols.Postgres => SqlSourceKind.Postgres,
        _ => throw new ArgumentException("Not a SQL connection.", nameof(c))
    };

    public static IReadOnlyList<ColumnSetting> Settings(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<ColumnSetting>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string? SettingsProblem(JsonNode? node, out string json)
    {
        json = "[]";
        if (node is null) return null;
        try
        {
            var settings = node.Deserialize<List<ColumnSetting>>(JsonOptions) ?? [];
            if (settings.Any(s => string.IsNullOrWhiteSpace(s.Column) || !Enum.IsDefined(s.Choice)))
                return "Each column setting needs a column and a choice of map, text or skip.";
            json = JsonSerializer.Serialize(settings, JsonOptions);
            return null;
        }
        catch (JsonException)
        {
            return "Each column setting needs a column and a choice of map, text or skip.";
        }
    }

    public static string? ConnectionStringProblem(SqlSourceKind kind, string connectionString, string ownDatabase)
    {
        try
        {
            if (kind == SqlSourceKind.Sqlite)
            {
                var source = new SqliteConnectionStringBuilder(connectionString).DataSource;
                if (source.Length == 0 || source.Contains(":memory:", StringComparison.Ordinal) || !Path.IsPathFullyQualified(source))
                    return "A SQLite source needs an absolute file path: Data Source=/path/to/file.db.";
                if (!File.Exists(source)) return $"No file at {source}.";
                var own = ownDatabase.Length == 0 || ownDatabase.Contains(":memory:", StringComparison.Ordinal) ? "" : Path.GetFullPath(ownDatabase);
                return string.Equals(Path.GetFullPath(source), own, StringComparison.Ordinal)
                    ? "A source cannot be Baseport's own database."
                    : null;
            }
            var hosts = Hosts(kind, connectionString);
            return hosts.Count == 0 ? "The connection string names no server." : hosts.Select(ProxyTarget.HostProblem).FirstOrDefault(p => p is not null);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return "The connection string could not be read.";
        }
    }

    internal static IReadOnlyList<string> Hosts(SqlSourceKind kind, string connectionString)
    {
        if (kind == SqlSourceKind.SqlServer)
        {
            var server = new SqlConnectionStringBuilder(connectionString).DataSource.Trim();
            if (server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) server = server[4..];
            var host = server.Split(',', '\\')[0].Trim();
            return host.Length == 0 ? [] : [host is "." or "(local)" or "(localdb)" ? "localhost" : host];
        }
        return (new NpgsqlConnectionStringBuilder(connectionString).Host ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.StartsWith('/') ? "localhost"
                : System.Net.IPAddress.TryParse(h, out _) ? h
                : h.StartsWith('[') && h.Contains(']') ? h[1..h.IndexOf(']')]
                : h.Split(':')[0])
            .ToList();
    }

    // ponytail: host checked before connect, not pinned; a DNS answer changing in between is not caught
    private static async Task<(DbConnection Connection, SqlSourceKind Kind)> OpenAsync(AppDbContext db, Connection c, CancellationToken ct)
    {
        var kind = Kind(c);
        var connectionString = await SecretStore.ResolveAsync(db, c.AuthSecretId, ct)
            ?? throw new RemoteFetch.FetchException($"The secret for connection '{c.Name}' is missing.");
        if (ConnectionStringProblem(kind, connectionString, db.Database.GetDbConnection().DataSource) is { } problem)
            throw new RemoteFetch.FetchException(problem);

        DbConnection conn = kind switch
        {
            SqlSourceKind.Sqlite => new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()),
            SqlSourceKind.SqlServer => new SqlConnection(new SqlConnectionStringBuilder(connectionString)
            {
                ApplicationIntent = ApplicationIntent.ReadOnly,
                Pooling = false,
                ConnectTimeout = (int)CommandTimeout.TotalSeconds
            }.ToString()),
            _ => new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Timeout = (int)CommandTimeout.TotalSeconds }.ToString())
        };
        try
        {
            await conn.OpenAsync(ct);
            return (conn, kind);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public static async Task<IReadOnlyList<SourceTable>> TablesAsync(AppDbContext db, Connection c, CancellationToken ct)
    {
        try
        {
            var (conn, kind) = await OpenAsync(db, c, ct);
            await using (conn)
                return await TablesAsync(conn, kind, ct);
        }
        catch (DbException ex)
        {
            throw new RemoteFetch.FetchException($"The database did not answer: {ex.Message}");
        }
    }

    private static async Task<IReadOnlyList<SourceTable>> TablesAsync(DbConnection conn, SqlSourceKind kind, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = kind switch
        {
            SqlSourceKind.Sqlite => "SELECT '', name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
            SqlSourceKind.SqlServer => "SELECT s.name, t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id ORDER BY s.name, t.name",
            _ => "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY 1, 2"
        };
        var tables = new List<SourceTable>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) tables.Add(new(reader.GetString(0), reader.GetString(1)));
        return tables;
    }

    public static async Task<(SourceTable Table, IReadOnlyList<SourceColumn> Columns)> DescribeAsync(AppDbContext db, Connection c, string table, CancellationToken ct)
    {
        try
        {
            var (conn, kind) = await OpenAsync(db, c, ct);
            await using (conn)
            {
                var found = await FindAsync(conn, kind, table, ct);
                return (found, await ColumnsAsync(conn, kind, found, ct));
            }
        }
        catch (DbException ex)
        {
            throw new RemoteFetch.FetchException($"The database did not answer: {ex.Message}");
        }
    }

    private static async Task<SourceTable> FindAsync(DbConnection conn, SqlSourceKind kind, string table, CancellationToken ct) =>
        (await TablesAsync(conn, kind, ct)).FirstOrDefault(t => t.Display == table.Trim())
            ?? throw new RemoteFetch.FetchException($"The source has no table '{table}'.");

    private static async Task<IReadOnlyList<SourceColumn>> ColumnsAsync(DbConnection conn, SqlSourceKind kind, SourceTable table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = kind switch
        {
            SqlSourceKind.Sqlite => "SELECT name, type, pk FROM pragma_table_info(@name) ORDER BY cid",
            SqlSourceKind.SqlServer => """
                SELECT c.name, ty.name + CASE WHEN c.max_length = -1 THEN '(max)' ELSE '' END,
                       CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END
                FROM sys.columns c
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                LEFT JOIN (SELECT ic.column_id FROM sys.indexes i
                           JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                           WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@name))) pk
                  ON pk.column_id = c.column_id
                WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@name))
                ORDER BY c.column_id
                """,
            _ => """
                SELECT a.attname, format_type(a.atttypid, a.atttypmod), CASE WHEN i.indrelid IS NULL THEN 0 ELSE 1 END
                FROM pg_attribute a
                LEFT JOIN pg_index i ON i.indrelid = a.attrelid AND i.indisprimary AND a.attnum = ANY(i.indkey)
                WHERE a.attrelid = (quote_ident(@schema) || '.' || quote_ident(@name))::regclass AND a.attnum > 0 AND NOT a.attisdropped
                ORDER BY a.attnum
                """
        };
        Parameter(cmd, "@schema", table.Schema);
        Parameter(cmd, "@name", table.Name);

        var columns = new List<SourceColumn>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var type = reader.IsDBNull(1) ? "" : reader.GetString(1);
            columns.Add(new(reader.GetString(0), type, FieldType(kind, type), Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture) > 0));
        }
        return columns;
    }

    public static string? FieldType(SqlSourceKind kind, string sourceType)
    {
        var t = sourceType.Trim().ToLowerInvariant();
        var bare = t.Split('(')[0].Trim();
        return kind switch
        {
            SqlSourceKind.Sqlite => bare switch
            {
                "" => "text",
                "date" => "date",
                "datetime" or "timestamp" => "datetime",
                "bool" or "boolean" => "boolean",
                _ when bare.Contains("int", StringComparison.Ordinal) => "number",
                _ when bare.Contains("char", StringComparison.Ordinal) || bare.Contains("clob", StringComparison.Ordinal) || bare == "text" => "text",
                _ when bare.Contains("real", StringComparison.Ordinal) || bare.Contains("floa", StringComparison.Ordinal)
                    || bare.Contains("doub", StringComparison.Ordinal) || bare is "numeric" or "decimal" => "number",
                _ => null
            },
            SqlSourceKind.SqlServer => bare switch
            {
                "bit" => "boolean",
                "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "float" or "real" => "number",
                "money" or "smallmoney" => "currency",
                "date" => "date",
                "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "datetime",
                "char" or "nchar" or "varchar" or "nvarchar" when !t.EndsWith("(max)", StringComparison.Ordinal) => "text",
                "varchar" or "nvarchar" or "text" or "ntext" or "xml" => "longtext",
                "uniqueidentifier" or "time" => "text",
                _ => null
            },
            _ => bare switch
            {
                "boolean" => "boolean",
                "smallint" or "integer" or "bigint" or "numeric" or "real" or "double precision" => "number",
                "money" => "currency",
                "date" => "date",
                "timestamp without time zone" or "timestamp with time zone" or "timestamp" => "datetime",
                "text" => "longtext",
                "character varying" or "character" or "varchar" or "char" or "uuid" or "citext" or "time without time zone" => "text",
                _ => null
            }
        };
    }

    public static IReadOnlyList<string> UnchosenColumns(IReadOnlyList<SourceColumn> columns, IReadOnlyList<ColumnSetting> settings) =>
        columns.Where(c => c.FieldType is null && !settings.Any(s => s.Column == c.Name && s.Choice is ColumnChoice.Text or ColumnChoice.Skip))
            .Select(c => c.Name).ToList();

    public static IReadOnlyList<FieldDefinition> Fields(IReadOnlyList<SourceColumn> columns, IReadOnlyList<ColumnSetting> settings)
    {
        var fields = new List<FieldDefinition>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var choice = Choice(column, settings);
            if (choice == ColumnChoice.Skip) continue;
            var name = DefinitionImport.SafeName(column.Name, fields.Count + 1);
            for (var dupe = 2; !used.Add(name); dupe++) name = $"{DefinitionImport.SafeName(column.Name, fields.Count + 1)}_{dupe}";
            fields.Add(new FieldDefinition
            {
                Name = name,
                Label = name == column.Name ? "" : column.Name,
                DataType = choice == ColumnChoice.Text ? "text" : column.FieldType ?? "text",
                Position = fields.Count
            });
        }
        return fields;
    }

    private static ColumnChoice Choice(SourceColumn column, IReadOnlyList<ColumnSetting> settings) =>
        settings.FirstOrDefault(s => s.Column == column.Name)?.Choice ?? (column.FieldType is null ? ColumnChoice.Skip : ColumnChoice.Map);

    public static async IAsyncEnumerable<RemoteFetch.Page> PagesAsync(
        AppDbContext db, Connection c, string table, IReadOnlyList<ColumnSetting> settings, RemoteFetch.Report report,
        int maxRows = RemoteFetch.MaxRows, int maxPages = RemoteFetch.MaxPages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var pages = ReadAsync(db, c, table, settings, report, maxRows, maxPages, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            try
            {
                if (!await pages.MoveNextAsync()) yield break;
            }
            catch (DbException ex)
            {
                throw new RemoteFetch.FetchException($"The database did not answer: {ex.Message}");
            }
            yield return pages.Current;
        }
    }

    private static async IAsyncEnumerable<RemoteFetch.Page> ReadAsync(
        AppDbContext db, Connection c, string table, IReadOnlyList<ColumnSetting> settings, RemoteFetch.Report report,
        int maxRows, int maxPages, [EnumeratorCancellation] CancellationToken ct)
    {
        var (conn, kind) = await OpenAsync(db, c, ct);
        await using var _ = conn;
        report.Strategy = "keyset";

        var source = await FindAsync(conn, kind, table, ct);
        var columns = await ColumnsAsync(conn, kind, source, ct);
        var keys = columns.Where(col => col.IsKey).ToList();
        if (keys.Count != 1)
            throw new RemoteFetch.FetchException($"'{source.Display}' has no single-column primary key, which reading it page by page needs.");
        if (UnchosenColumns(columns, settings) is { Count: > 0 } open)
            throw new RemoteFetch.FetchException($"Choose text or skip for the columns Baseport cannot map: {string.Join(", ", open)}.");

        var key = keys[0];
        var selected = columns.Where(col => Choice(col, settings) != ColumnChoice.Skip || col.IsKey).ToList();
        var list = string.Join(", ", selected.Select(col => Quote(kind, col.Name)));
        var from = source.Schema.Length == 0 ? Quote(kind, source.Name) : $"{Quote(kind, source.Schema)}.{Quote(kind, source.Name)}";

        await using var tx = await BeginAsync(conn, kind, report, ct);

        object? last = null;
        var url = new Uri($"{c.Protocol}:{Uri.EscapeDataString(source.Display)}");
        for (var number = 1; ; number++)
        {
            var take = Math.Min(PageSize, maxRows - report.Rows);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandTimeout = (int)CommandTimeout.TotalSeconds;
            var where = last is null ? "" : $" WHERE {Quote(kind, key.Name)} > @last";
            cmd.CommandText = kind == SqlSourceKind.SqlServer
                ? $"SELECT TOP (@take) {list} FROM {from}{where} ORDER BY {Quote(kind, key.Name)}"
                : $"SELECT {list} FROM {from}{where} ORDER BY {Quote(kind, key.Name)} LIMIT @take";
            Parameter(cmd, "@take", take + 1);
            if (last is not null) Parameter(cmd, "@last", last);

            var rows = new List<JsonObject>();
            var more = false;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (rows.Count == take)
                    {
                        more = true;
                        break;
                    }
                    var row = new JsonObject();
                    for (var i = 0; i < selected.Count; i++)
                    {
                        var column = selected[i];
                        if (column.Name == key.Name) last = reader.GetValue(i);
                        var choice = Choice(column, settings);
                        if (choice == ColumnChoice.Skip || reader.IsDBNull(i)) continue;
                        row[column.Name] = Value(reader.GetValue(i), choice == ColumnChoice.Text ? "text" : column.FieldType ?? "text");
                    }
                    rows.Add(row);
                }
            }

            report.Pages = number;
            report.Rows += rows.Count;
            if (rows.Count > 0) yield return new RemoteFetch.Page(number, rows, "keyset", url);
            if (!more) yield break;
            if (report.Rows >= maxRows) { report.Ceiling = $"Stopped at {maxRows:N0} rows."; yield break; }
            if (number >= maxPages) { report.Ceiling = $"Stopped at {maxPages:N0} pages."; yield break; }
        }
    }

    private static async Task<DbTransaction> BeginAsync(DbConnection conn, SqlSourceKind kind, RemoteFetch.Report report, CancellationToken ct)
    {
        switch (kind)
        {
            case SqlSourceKind.Postgres:
                var pg = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
                await using (var readOnly = conn.CreateCommand())
                {
                    readOnly.Transaction = pg;
                    readOnly.CommandText = "SET TRANSACTION READ ONLY";
                    await readOnly.ExecuteNonQueryAsync(ct);
                }
                return pg;
            case SqlSourceKind.SqlServer:
                await using (var state = conn.CreateCommand())
                {
                    state.CommandText = "SELECT snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()";
                    if (Convert.ToInt32(await state.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1)
                        return await conn.BeginTransactionAsync(IsolationLevel.Snapshot, ct);
                }
                report.Inconsistent = true;
                return await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            default:
                return await conn.BeginTransactionAsync(ct);
        }
    }

    internal static JsonNode? Value(object value, string fieldType) => (value, fieldType) switch
    {
        (byte[] bytes, _) => JsonValue.Create(Convert.ToBase64String(bytes)),
        (bool b, "boolean") => JsonValue.Create(b),
        (long or int or short or byte, "boolean") => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0),
        (DateOnly d, _) => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        (DateTime d, "date") => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        (DateTime d, _) => JsonValue.Create(d.ToString(d.Kind == DateTimeKind.Unspecified ? "yyyy-MM-ddTHH:mm:ss.FFFFFFF" : "o", CultureInfo.InvariantCulture)),
        (DateTimeOffset d, _) => JsonValue.Create(d.ToString("o", CultureInfo.InvariantCulture)),
        (double d, "number" or "currency") when double.IsFinite(d) => JsonValue.Create(d),
        (float f, "number" or "currency") when float.IsFinite(f) => JsonValue.Create((double)f),
        (decimal or long or int or short or byte or sbyte or ulong or uint or ushort, "number" or "currency") =>
            JsonValue.Create(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    private static string Quote(SqlSourceKind kind, string identifier) => kind == SqlSourceKind.SqlServer
        ? $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]"
        : $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void Parameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
