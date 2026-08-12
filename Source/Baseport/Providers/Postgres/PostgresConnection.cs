using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Baseport.Providers;

namespace Baseport.Providers.Postgres;

// one postgres wire-protocol (v3) session: startup, cleartext-password auth against an api token, then simple-query and extended-query (parse/bind/describe/execute/sync) messages answered from SqlEngine.ReadAsync
// ponytail: no ssl, no catalog emulation (pg_catalog/information_schema), extended query only for statements with zero bound parameters (covers driver-issued begin/commit/rollback and plain ad-hoc selects; real $1-style parameter binding is not implemented), results always sent in text format even if a client asks for binary
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

        var statements = new Dictionary<string, string>();
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
                    await RunQueryAsync(stream, scopes, ReadCStringFromStart(m.Payload), ct);
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
                    await HandleDescribeAsync(stream, m.Payload, scopes, portals, ct);
                    break;

                case 'E' when !hadError:
                    hadError = await HandleExecuteAsync(stream, m.Payload, scopes, portals, ct);
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

    // dbeaver/jdbc probe pg_catalog on connect; sqlite has none of it and can't even parse some of the syntax, so this is caught before sqlite ever sees it
    // ponytail: answers "nothing here" instead of erroring — schema browser trees stay empty, no real catalog behind this
    private static readonly Regex CatalogQuery = new(@"\b(FROM|JOIN)\s+(pg_catalog\.|information_schema\.|pg_\w+\b)", RegexOptions.IgnoreCase);

    private static async Task<SqlEngine.Result> ExecuteAsync(IServiceScopeFactory scopes, string sql)
    {
        // zero columns reads as "not a result set" to real clients (pg8000: "no result set", dbeaver: SQLSTATE 02000); one placeholder column is what an empty SELECT actually looks like on the wire
        if (CatalogQuery.IsMatch(sql)) return new SqlEngine.Result(["?column?"], [], false, null);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invalid = SqlEngine.Validate(sql);
        return invalid is null ? await SqlEngine.ReadAsync(db, sql) : new SqlEngine.Result([], [], false, invalid);
    }

    private static async Task RunQueryAsync(NetworkStream stream, IServiceScopeFactory scopes, string sql, CancellationToken ct)
    {
        if (NoOpTag(sql) is { } noOpTag) { await WriteCommandCompleteAsync(stream, noOpTag, ct); return; }

        var result = await ExecuteAsync(scopes, sql);

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

    private static void HandleParse(byte[] payload, Dictionary<string, string> statements)
    {
        var i = 0;
        var name = ReadCString(payload, ref i);
        var sql = ReadCString(payload, ref i);
        statements[name] = sql;
    }

    private static string? HandleBind(byte[] payload, Dictionary<string, string> statements, Dictionary<string, Portal> portals)
    {
        var i = 0;
        var portalName = ReadCString(payload, ref i);
        var stmtName = ReadCString(payload, ref i);
        var formatCodeCount = ReadI16(payload, ref i);
        i += formatCodeCount * 2;
        var paramCount = ReadI16(payload, ref i);
        if (paramCount > 0) return "parameterized queries are not supported";
        if (!statements.TryGetValue(stmtName, out var sql)) return $"unknown statement \"{stmtName}\"";
        portals[portalName] = new Portal { Sql = sql };
        return null;
    }

    private static async Task HandleDescribeAsync(NetworkStream stream, byte[] payload, IServiceScopeFactory scopes, Dictionary<string, Portal> portals, CancellationToken ct)
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

        await EnsureExecutedAsync(portal, scopes, ct);
        if (portal.NoOpCommandTag is not null || portal.Result is not { Columns.Count: > 0 })
            await WriteMessageAsync(stream, (byte)'n', [], ct); // nodata
        else
            await WriteRowDescriptionAsync(stream, portal.Result.Columns, ct);
    }

    // returns whether the connection is now in an error state (pending a sync to clear)
    private static async Task<bool> HandleExecuteAsync(NetworkStream stream, byte[] payload, IServiceScopeFactory scopes, Dictionary<string, Portal> portals, CancellationToken ct)
    {
        var i = 0;
        var name = ReadCString(payload, ref i);
        if (!portals.TryGetValue(name, out var portal))
        {
            await WriteErrorAsync(stream, "ERROR", "34000", $"unknown portal \"{name}\"", ct);
            return true;
        }

        await EnsureExecutedAsync(portal, scopes, ct);
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

    private static void HandleClose(byte[] payload, Dictionary<string, string> statements, Dictionary<string, Portal> portals)
    {
        var i = 0;
        var target = (char)payload[i++];
        var name = ReadCString(payload, ref i);
        if (target == 'S') statements.Remove(name); else portals.Remove(name);
    }

    // reads (or, for a no-op statement, tags) the portal's outcome the first time it's touched by describe or execute, so either can come first
    private static async Task EnsureExecutedAsync(Portal portal, IServiceScopeFactory scopes, CancellationToken ct)
    {
        if (portal.Executed) return;
        portal.Executed = true;

        if (NoOpTag(portal.Sql) is { } noOpTag) { portal.NoOpCommandTag = noOpTag; return; }

        portal.Result = await ExecuteAsync(scopes, portal.Sql);
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
