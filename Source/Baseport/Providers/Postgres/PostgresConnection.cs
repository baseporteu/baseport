using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Baseport.Providers;

namespace Baseport.Providers.Postgres;

// one postgres wire-protocol (v3) session: startup, cleartext-password auth against an api token, then simple-query and extended-query (parse/bind/describe/execute/sync) messages answered from SqlEngine.ReadAsync
// ponytail: no ssl, catalog emulated from WireCatalog rather than real (an object it does not cover answers empty), bound parameters inlined as literals and only in text format, results always sent in text format even if a client asks for binary
public static class PostgresConnection
{
    public static async Task HandleAsync(Socket socket, IServiceScopeFactory scopes, CancellationToken ct)
    {
        socket.NoDelay = true;
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        if (await ReadStartupAsync(stream, ct) is null) return;

        await WriteAuthAsync(stream, 3, ct); // authtype 3: cleartext password
        var passwordMessage = await ReadMessageAsync(stream, ct);
        if (passwordMessage is not { Type: 'p' } pm)
        {
            await WriteErrorAsync(stream, "FATAL", "08P01", "expected a password message", ct);
            return;
        }

        var token = ReadCStringFromStart(pm.Payload);
        var account = await ResolveAccountAsync(scopes, token, ct);
        if (account is null)
        {
            await WriteErrorAsync(stream, "FATAL", "28P01", "password authentication failed", ct);
            return;
        }

        await WriteAuthAsync(stream, 0, ct); // authtype 0: ok
        await WriteParameterStatusAsync(stream, "server_version", "15.0 (Baseport)", ct);
        await WriteParameterStatusAsync(stream, "client_encoding", "UTF8", ct);
        await WriteBackendKeyDataAsync(stream, ct);
        await WriteReadyForQueryAsync(stream, ct);

        var statements = new Dictionary<string, PreparedStatement>();
        var portals = new Dictionary<string, Portal>();
        var hadError = false;

        while (true)
        {
            var msg = await ReadMessageAsync(stream, ct);
            if (msg is not { } m) return;

            switch (m.Type)
            {
                case 'X':
                    return;

                case 'Q':
                    await RunQueryAsync(stream, scopes, ReadCStringFromStart(m.Payload), account.Id, ct);
                    await WriteReadyForQueryAsync(stream, ct);
                    break;

                case 'P' when !hadError:
                    HandleParse(m.Payload, statements);
                    await WriteMessageAsync(stream, (byte)'1', [], ct); // parsecomplete
                    break;

                case 'B' when !hadError:
                    var bindError = HandleBind(m.Payload, statements, portals);
                    if (bindError is null) await WriteMessageAsync(stream, (byte)'2', [], ct); // bindcomplete
                    else { await WriteErrorAsync(stream, "ERROR", "0A000", bindError, ct); hadError = true; }
                    break;

                case 'D' when !hadError:
                    await HandleDescribeAsync(stream, m.Payload, scopes, portals, account.Id, ct);
                    break;

                case 'E' when !hadError:
                    hadError = await HandleExecuteAsync(stream, m.Payload, scopes, portals, account.Id, ct);
                    break;

                case 'C':
                    HandleClose(m.Payload, statements, portals);
                    await WriteMessageAsync(stream, (byte)'3', [], ct); // closecomplete
                    break;

                case 'H':
                    break; // flush: nothing buffered on our side to flush

                case 'S':
                    hadError = false;
                    await WriteReadyForQueryAsync(stream, ct);
                    break;

                case 'P' or 'B' or 'D' or 'E':
                    break; // already in an error state: skip until sync, per the extended-query protocol

                default:
                    await WriteErrorAsync(stream, "ERROR", "0A000", $"unsupported message type '{m.Type}'", ct);
                    hadError = true;
                    break;
            }
        }
    }

    private static async Task<UserAccount?> ResolveAccountAsync(IServiceScopeFactory scopes, string token, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await ApiAuth.ResolveByTokenAsync(db, token);
    }

    // every real driver (pg8000, psycopg2, jdbc, dbeaver) wraps queries in begin/commit by default, and most also send session-configuration statements (set/reset) on connect or per query; there is nothing to commit, roll back, or configure against a read-only, always-autocommit, session-less engine, so these are accepted as no-ops instead of tripping the read-only allowlist
    private static readonly (Regex Pattern, string Tag)[] NoOpStatements =
    [
        (new Regex(@"^\s*(BEGIN|START\s+TRANSACTION)\b", RegexOptions.IgnoreCase), "BEGIN"),
        (new Regex(@"^\s*COMMIT\b", RegexOptions.IgnoreCase), "COMMIT"),
        (new Regex(@"^\s*ROLLBACK\b", RegexOptions.IgnoreCase), "ROLLBACK"),
        (new Regex(@"^\s*(SET|RESET)\b", RegexOptions.IgnoreCase), "SET"),
    ];

    private static string? NoOpTag(string sql) => NoOpStatements.FirstOrDefault(x => x.Pattern.IsMatch(sql)).Tag;

    // WireCatalog answers the catalog objects a browser actually reads; this is the net under it, so an unemulated pg_* object still answers empty instead of aborting the client with a sqlite error
    private static readonly Regex CatalogQuery = new(@"\b(FROM|JOIN)\s+(pg_catalog\.|information_schema\.|pg_\w+\b)", RegexOptions.IgnoreCase);

    // pgjdbc asks for these during connection setup, before a client ever browses anything; SHOW is neither a no-op nor on SqlEngine's allowlist, so it used to come back as an error and abort dbeaver's connect
    private static readonly Regex ShowStatement = new(@"^\s*SHOW\s+(?<name>[A-Za-z_][A-Za-z0-9_\s]*?)\s*;?\s*$", RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, (string Column, string Value)> Settings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["search_path"] = ("search_path", "public"),
        ["transaction isolation level"] = ("transaction_isolation", "read committed"),
        ["transaction_isolation"] = ("transaction_isolation", "read committed"),
        ["server_version"] = ("server_version", "15.0"),
        ["client_encoding"] = ("client_encoding", "UTF8"),
        ["standard_conforming_strings"] = ("standard_conforming_strings", "on"),
        ["datestyle"] = ("DateStyle", "ISO, MDY"),
        ["timezone"] = ("TimeZone", "UTC"),
        ["integer_datetimes"] = ("integer_datetimes", "on"),
    };

    private static SqlEngine.Result? ShowResult(string sql)
    {
        var match = ShowStatement.Match(sql);
        if (!match.Success) return null;

        var name = Regex.Replace(match.Groups["name"].Value.Trim(), @"\s+", " ");
        // an unknown setting answers empty rather than erroring: a client probing one it can live without must not lose the connection over it
        return Settings.TryGetValue(name, out var setting)
            ? new SqlEngine.Result([setting.Column], [[setting.Value]], false, null)
            : new SqlEngine.Result([name], [[""]], false, null);
    }

    // sqlite has no version()/current_schema()/current_database(); dbeaver reads all three to identify the server and pick the schema to hang its tree under, and a client with no current database has nothing to browse
    private static void RegisterCompatibilityFunctions(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        conn.CreateFunction("version", () => "PostgreSQL 15.0 (Baseport)");
        conn.CreateFunction("current_schema", () => "public");
        conn.CreateFunction("current_database", () => "baseport");
        conn.CreateFunction("current_user", () => "baseport");
        conn.CreateFunction("session_user", () => "baseport");
        conn.CreateFunction("pg_backend_pid", () => 0);
    }

    // sqlite names a result column after the expression that produced it, so version() comes back as "version()"; postgres calls it "version", and a client reading that column by name finds nothing otherwise
    private static readonly Regex ZeroArgumentCall = new(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\(\)$");

    private static SqlEngine.Result NameColumnsLikePostgres(SqlEngine.Result result) =>
        result.Columns.Any(ZeroArgumentCall.IsMatch)
            ? result with
            {
                Columns = result.Columns
                    .Select(c => ZeroArgumentCall.Match(c) is { Success: true } m ? m.Groups["name"].Value : c)
                    .ToList()
            }
            : result;

    private static async Task<SqlEngine.Result> ExecuteAsync(IServiceScopeFactory scopes, string sql, string userId)
    {
        if (ShowResult(sql) is { } show) return show;
        sql = StripCasts(sql);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invalid = SqlEngine.Validate(sql);
        if (invalid is not null) return new SqlEngine.Result([], [], false, invalid);

        var result = await SqlEngine.ReadAsync(db, sql, conn =>
        {
            RegisterCompatibilityFunctions(conn);
            WireCatalog.Apply(conn, WireDialect.Postgres, userId);
        });

        // the catalog covers what a browser reads; anything else under pg_catalog still answers empty rather than handing back a sqlite error
        return result.Error is not null && CatalogQuery.IsMatch(sql)
            ? new SqlEngine.Result(["?column?"], [], false, null)
            : NameColumnsLikePostgres(result);
    }

    private static async Task RunQueryAsync(NetworkStream stream, IServiceScopeFactory scopes, string sql, string userId, CancellationToken ct)
    {
        if (NoOpTag(sql) is { } noOpTag) { await WriteCommandCompleteAsync(stream, noOpTag, ct); return; }

        var result = await ExecuteAsync(scopes, sql, userId);

        if (result.Error is not null)
        {
            await WriteErrorAsync(stream, "ERROR", "42601", result.Error, ct);
            return;
        }

        await WriteRowDescriptionAsync(stream, result.Columns, ct);
        foreach (var row in result.Rows) await WriteDataRowAsync(stream, row, ct);
        await WriteCommandCompleteAsync(stream, $"SELECT {result.Rows.Count}", ct);
    }

    // extended query protocol (parse/bind/describe/execute/sync/close)

    private sealed class Portal
    {
        public required string Sql;
        public bool Executed;
        public string? NoOpCommandTag;
        public SqlEngine.Result? Result;
    }

    // the parameter type oids arrive here, not on bind, and a binary value cannot be decoded without them
    private sealed record PreparedStatement(string Sql, int[] ParameterTypes);

    private static void HandleParse(byte[] payload, Dictionary<string, PreparedStatement> statements)
    {
        var i = 0;
        var name = ReadCString(payload, ref i);
        var sql = ReadCString(payload, ref i);

        var typeCount = i + 2 <= payload.Length ? ReadI16(payload, ref i) : (short)0;
        var types = new int[typeCount];
        for (var t = 0; t < typeCount && i + 4 <= payload.Length; t++)
        {
            types[t] = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(i, 4));
            i += 4;
        }

        statements[name] = new PreparedStatement(sql, types);
    }

    private static string? HandleBind(byte[] payload, Dictionary<string, PreparedStatement> statements, Dictionary<string, Portal> portals)
    {
        var i = 0;
        var portalName = ReadCString(payload, ref i);
        var stmtName = ReadCString(payload, ref i);

        if (!statements.TryGetValue(stmtName, out var statement)) return $"unknown statement \"{stmtName}\"";

        var formatCodeCount = ReadI16(payload, ref i);
        var formatCodes = new short[formatCodeCount];
        for (var f = 0; f < formatCodeCount; f++) formatCodes[f] = ReadI16(payload, ref i);

        var paramCount = ReadI16(payload, ref i);
        var values = new List<string?>(paramCount);
        for (var p = 0; p < paramCount; p++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(i, 4));
            i += 4;
            if (length < 0) { values.Add(null); continue; }

            // one format code applies to every parameter, none at all means text
            var format = formatCodeCount == 0 ? 0 : formatCodes[formatCodeCount == 1 ? 0 : p];
            var typeOid = p < statement.ParameterTypes.Length ? statement.ParameterTypes[p] : 0;

            var decoded = format == 0
                ? Encoding.UTF8.GetString(payload, i, length)
                : DecodeBinary(payload.AsSpan(i, length), typeOid);
            if (decoded is null) return $"parameter type {typeOid} cannot be read in binary format";

            values.Add(decoded);
            i += length;
        }

        portals[portalName] = new Portal { Sql = Inline(statement.Sql, values) };
        return null;
    }

    // pgjdbc binds ints in binary, which is what dbeaver's table and column queries pass their namespace and relation oids as
    private static string? DecodeBinary(ReadOnlySpan<byte> value, int typeOid) => typeOid switch
    {
        21 when value.Length == 2 => BinaryPrimitives.ReadInt16BigEndian(value).ToString(),
        23 or 26 when value.Length == 4 => BinaryPrimitives.ReadInt32BigEndian(value).ToString(),
        20 when value.Length == 8 => BinaryPrimitives.ReadInt64BigEndian(value).ToString(),
        700 when value.Length == 4 => BinaryPrimitives.ReadSingleBigEndian(value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        701 when value.Length == 8 => BinaryPrimitives.ReadDoubleBigEndian(value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        16 when value.Length == 1 => value[0] == 0 ? "0" : "1",
        25 or 1043 or 19 or 18 or 705 or 0 => Encoding.UTF8.GetString(value),
        _ => null,
    };

    // The engine takes one finished statement, so a bound value becomes a literal in it. Everything here is read-only and already allowlisted, and the value is escaped on the way in.
    internal static string Inline(string sql, List<string?> values)
    {
        if (values.Count == 0) return sql;

        return Rewrite(sql, token =>
        {
            if (token[0] != '$') return null;
            var index = int.Parse(token[1..]) - 1;
            if (index < 0 || index >= values.Count) return null;

            var value = values[index];
            if (value is null) return "NULL";
            // sqlite compares a number to a text column as unequal, and the catalog's oids are numbers, so a numeric parameter must not arrive quoted
            return long.TryParse(value, out _) || double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
                ? value
                : $"'{value.Replace("'", "''")}'";
        });
    }

    // dbeaver casts all over its metadata queries ('pg_namespace'::regclass, nspname::text); sqlite has no :: operator, and the cast is noise once the value is already the right shape
    internal static string StripCasts(string sql) => Rewrite(sql, token => token.StartsWith("::", StringComparison.Ordinal) ? "" : null);

    // Walks the statement outside string literals, quoted identifiers and comments, so a value that happens to contain :: or $1 is left alone.
    private static string Rewrite(string sql, Func<string, string?> replace)
    {
        var output = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (c is '\'' or '"')
            {
                var quote = c;
                var start = i++;
                while (i < sql.Length)
                {
                    if (sql[i] == quote && i + 1 < sql.Length && sql[i + 1] == quote) { i += 2; continue; }
                    if (sql[i] == quote) { i++; break; }
                    i++;
                }
                output.Append(sql, start, i - start);
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var start = i;
                while (i < sql.Length && sql[i] != '\n') i++;
                output.Append(sql, start, i - start);
                continue;
            }

            if (c == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                var start = i;
                i += 2;
                while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
                if (i < sql.Length && sql[i] == '"')
                {
                    i++;
                    while (i < sql.Length && sql[i] != '"') i++;
                    if (i < sql.Length) i++;
                }
                else while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                // a type name can be two words (character varying, double precision) and can carry an array suffix
                var trailing = i;
                while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
                var word = i;
                while (i < sql.Length && char.IsAsciiLetter(sql[i])) i++;
                if (i > word && TwoWordTypes.Contains(sql[word..i])) trailing = i;
                i = trailing;
                while (i < sql.Length && (sql[i] == '[' || sql[i] == ']' || char.IsWhiteSpace(sql[i])) && sql[i] != '\n')
                {
                    if (sql[i] == '[' || sql[i] == ']') i++;
                    else if (i + 1 < sql.Length && sql[i + 1] == '[') i++;
                    else break;
                }

                output.Append(replace(sql[start..i]) ?? sql[start..i]);
                continue;
            }

            if (c == '$' && i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1]))
            {
                var start = i++;
                while (i < sql.Length && char.IsAsciiDigit(sql[i])) i++;
                output.Append(replace(sql[start..i]) ?? sql[start..i]);
                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    private static readonly HashSet<string> TwoWordTypes = new(StringComparer.OrdinalIgnoreCase) { "varying", "precision" };

    private static async Task HandleDescribeAsync(NetworkStream stream, byte[] payload, IServiceScopeFactory scopes, Dictionary<string, Portal> portals, string userId, CancellationToken ct)
    {
        var i = 0;
        var target = (char)payload[i++];
        var name = ReadCString(payload, ref i);

        if (target != 'P')
        {
            // describing a statement (not yet bound to a portal): we don't type-infer placeholders ahead of bind
            await WriteMessageAsync(stream, (byte)'t', BuildEmptyParameterDescription(), ct);
            await WriteMessageAsync(stream, (byte)'n', [], ct); // nodata
            return;
        }

        if (!portals.TryGetValue(name, out var portal))
        {
            await WriteErrorAsync(stream, "ERROR", "34000", $"unknown portal \"{name}\"", ct);
            return;
        }

        await EnsureExecutedAsync(portal, scopes, userId, ct);
        if (portal.NoOpCommandTag is not null || portal.Result is not { Columns.Count: > 0 })
            await WriteMessageAsync(stream, (byte)'n', [], ct); // nodata
        else
            await WriteRowDescriptionAsync(stream, portal.Result.Columns, ct);
    }

    // returns whether the connection is now in an error state (pending a sync to clear)
    private static async Task<bool> HandleExecuteAsync(NetworkStream stream, byte[] payload, IServiceScopeFactory scopes, Dictionary<string, Portal> portals, string userId, CancellationToken ct)
    {
        var i = 0;
        var name = ReadCString(payload, ref i);
        if (!portals.TryGetValue(name, out var portal))
        {
            await WriteErrorAsync(stream, "ERROR", "34000", $"unknown portal \"{name}\"", ct);
            return true;
        }

        await EnsureExecutedAsync(portal, scopes, userId, ct);
        if (portal.NoOpCommandTag is not null)
        {
            await WriteCommandCompleteAsync(stream, portal.NoOpCommandTag, ct);
            return false;
        }
        if (portal.Result!.Error is not null)
        {
            await WriteErrorAsync(stream, "ERROR", "42601", portal.Result.Error, ct);
            return true;
        }

        foreach (var row in portal.Result.Rows) await WriteDataRowAsync(stream, row, ct);
        await WriteCommandCompleteAsync(stream, $"SELECT {portal.Result.Rows.Count}", ct);
        return false;
    }

    private static void HandleClose(byte[] payload, Dictionary<string, PreparedStatement> statements, Dictionary<string, Portal> portals)
    {
        var i = 0;
        var target = (char)payload[i++];
        var name = ReadCString(payload, ref i);
        if (target == 'S') statements.Remove(name); else portals.Remove(name);
    }

    // reads (or, for a no-op statement, tags) the portal's outcome the first time it's touched by describe or execute, so either can come first
    private static async Task EnsureExecutedAsync(Portal portal, IServiceScopeFactory scopes, string userId, CancellationToken ct)
    {
        if (portal.Executed) return;
        portal.Executed = true;

        if (NoOpTag(portal.Sql) is { } noOpTag) { portal.NoOpCommandTag = noOpTag; return; }

        portal.Result = await ExecuteAsync(scopes, portal.Sql, userId);
    }

    private static byte[] BuildEmptyParameterDescription()
    {
        using var ms = new MemoryStream();
        WriteI16(ms, 0);
        return ms.ToArray();
    }

    // reads (and answers) sslrequest/gssencrequest probes until the real startup message arrives
    private static async Task<Dictionary<string, string>?> ReadStartupAsync(NetworkStream stream, CancellationToken ct)
    {
        while (true)
        {
            var lenBuf = await stream.ReadExactAsync(4, ct);
            if (lenBuf is null) return null;
            var length = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            var payload = await stream.ReadExactAsync(length - 4, ct);
            if (payload is null) return null;

            var code = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
            if (code == 80877103 || code == 80877104) // sslrequest / gssencrequest
            {
                await stream.WriteAsync((byte[])[(byte)'N'], ct);
                continue;
            }

            var parms = new Dictionary<string, string>();
            var i = 4;
            while (i < payload.Length && payload[i] != 0)
            {
                var key = ReadCString(payload, ref i);
                var value = ReadCString(payload, ref i);
                parms[key] = value;
            }
            return parms;
        }
    }

    // message framing

    private static async Task<(char Type, byte[] Payload)?> ReadMessageAsync(NetworkStream stream, CancellationToken ct)
    {
        var typeBuf = await stream.ReadExactAsync(1, ct);
        if (typeBuf is null) return null;
        var lenBuf = await stream.ReadExactAsync(4, ct);
        if (lenBuf is null) return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        var payload = await stream.ReadExactAsync(length - 4, ct);
        if (payload is null) return null;
        return ((char)typeBuf[0], payload);
    }

    private static string ReadCString(byte[] buf, ref int i)
    {
        var start = i;
        while (i < buf.Length && buf[i] != 0) i++;
        var s = Encoding.UTF8.GetString(buf, start, i - start);
        i++;
        return s;
    }

    private static string ReadCStringFromStart(byte[] buf)
    {
        var i = 0;
        return ReadCString(buf, ref i);
    }

    private static short ReadI16(byte[] buf, ref int i)
    {
        var v = BinaryPrimitives.ReadInt16BigEndian(buf.AsSpan(i, 2));
        i += 2;
        return v;
    }

    private static async Task WriteMessageAsync(NetworkStream stream, byte type, byte[] payload, CancellationToken ct)
    {
        var header = new byte[5];
        header[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length + 4);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
    }

    private static void WriteCStr(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        ms.Write(bytes);
        ms.WriteByte(0);
    }

    private static void WriteI32(MemoryStream ms, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteI16(MemoryStream ms, short value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buf, value);
        ms.Write(buf);
    }

    // backend messages

    private static Task WriteAuthAsync(NetworkStream stream, int authType, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WriteI32(ms, authType);
        return WriteMessageAsync(stream, (byte)'R', ms.ToArray(), ct);
    }

    private static Task WriteParameterStatusAsync(NetworkStream stream, string name, string value, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WriteCStr(ms, name);
        WriteCStr(ms, value);
        return WriteMessageAsync(stream, (byte)'S', ms.ToArray(), ct);
    }

    private static Task WriteBackendKeyDataAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WriteI32(ms, Environment.ProcessId);
        WriteI32(ms, 0); // secret key: unused, we don't support cancelrequest
        return WriteMessageAsync(stream, (byte)'K', ms.ToArray(), ct);
    }

    private static Task WriteReadyForQueryAsync(NetworkStream stream, CancellationToken ct) =>
        WriteMessageAsync(stream, (byte)'Z', (byte[])[(byte)'I'], ct);

    private static Task WriteErrorAsync(NetworkStream stream, string severity, string sqlState, string message, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)'S'); WriteCStr(ms, severity);
        ms.WriteByte((byte)'C'); WriteCStr(ms, sqlState);
        ms.WriteByte((byte)'M'); WriteCStr(ms, message);
        ms.WriteByte(0);
        return WriteMessageAsync(stream, (byte)'E', ms.ToArray(), ct);
    }

    private static Task WriteRowDescriptionAsync(NetworkStream stream, List<string> columns, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WriteI16(ms, (short)columns.Count);
        foreach (var col in columns)
        {
            WriteCStr(ms, col);
            WriteI32(ms, 0);   // table oid: none
            WriteI16(ms, 0);   // column attr number: none
            WriteI32(ms, 25);  // type oid: text
            WriteI16(ms, -1);  // type size: variable
            WriteI32(ms, -1);  // type modifier: none
            WriteI16(ms, 0);   // format code: text
        }
        return WriteMessageAsync(stream, (byte)'T', ms.ToArray(), ct);
    }

    private static Task WriteDataRowAsync(NetworkStream stream, List<string?> row, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WriteI16(ms, (short)row.Count);
        foreach (var value in row)
        {
            if (value is null) { WriteI32(ms, -1); continue; }
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteI32(ms, bytes.Length);
            ms.Write(bytes);
        }
        return WriteMessageAsync(stream, (byte)'D', ms.ToArray(), ct);
    }

    private static Task WriteCommandCompleteAsync(NetworkStream stream, string tag, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WriteCStr(ms, tag);
        return WriteMessageAsync(stream, (byte)'C', ms.ToArray(), ct);
    }
}
