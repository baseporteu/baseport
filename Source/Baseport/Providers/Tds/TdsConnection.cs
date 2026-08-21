using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Baseport.Providers;

namespace Baseport.Providers.Tds;

// one tds (sql server wire protocol) session: prelogin, login7 with the password field carrying an api token, then sqlbatch requests answered from SqlEngine.ReadAsync as colmetadata/row/done tokens
// ponytail: sql-auth login only (no windows/sspi), no tls, sqlbatch only (no rpc/prepared statements), fixed 4096-byte response packets, columns reported as nvarchar(4000) with values truncated past that — upgrade to plp/nvarchar(max) streaming if a client needs longer values
public static class TdsConnection
{
    private const byte PtPreLogin = 0x12;
    private const byte PtLogin7 = 0x10;
    private const byte PtSqlBatch = 0x01;
    private const byte PtTabularResult = 0x04;
    private const int MaxPacketPayload = 4088; // 4096-byte default packet size minus the 8-byte header

    public static async Task HandleAsync(Socket socket, IServiceScopeFactory scopes, CancellationToken ct)
    {
        socket.NoDelay = true;
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var prelogin = await ReadTdsMessageAsync(stream, ct);
        if (prelogin is not { Type: PtPreLogin }) return;
        await WriteTdsMessageAsync(stream, PtTabularResult, BuildPreloginResponse(), ct);

        var login = await ReadTdsMessageAsync(stream, ct);
        if (login is not { Type: PtLogin7 } loginMsg) return;

        var (_, password) = ParseLogin7(loginMsg.Payload);
        var account = await ResolveAccountAsync(scopes, password, ct);
        if (account is null)
        {
            using var fail = new MemoryStream();
            WriteError(fail, "Login failed.");
            WriteDone(fail, 0x0002, 0, 0); // done_error
            await WriteTdsMessageAsync(stream, PtTabularResult, fail.ToArray(), ct);
            return;
        }

        using (var ok = new MemoryStream())
        {
            WriteLoginAck(ok);
            WriteDone(ok, 0x0000, 0, 0);
            await WriteTdsMessageAsync(stream, PtTabularResult, ok.ToArray(), ct);
        }

        while (true)
        {
            var msg = await ReadTdsMessageAsync(stream, ct);
            if (msg is not { } m) return;

            if (m.Type != PtSqlBatch)
            {
                using var err = new MemoryStream();
                WriteError(err, "only SQL batch requests are supported");
                WriteDone(err, 0x0002, 0, 0);
                await WriteTdsMessageAsync(stream, PtTabularResult, err.ToArray(), ct);
                continue;
            }

            await RunQueryAsync(stream, scopes, ExtractBatchText(m.Payload), account.Id, ct);
        }
    }

    private static async Task<UserAccount?> ResolveAccountAsync(IServiceScopeFactory scopes, string token, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await ApiAuth.ResolveByTokenAsync(db, token);
    }

    // real clients (pytds, sqlcmd, ssms) send these on connect or per statement: use [db] to pick a database, set ... to configure session options; there is only one database and no session state to configure, both are accepted as no-ops instead of tripping the read-only allowlist
    private static readonly Regex NoOpStatement = new(@"^\s*(USE\b|SET\s+\w)", RegexOptions.IgnoreCase);

    private static readonly Regex CatalogQuery = new(@"\b(FROM|JOIN)\s+(sys\.|INFORMATION_SCHEMA\.)", RegexOptions.IgnoreCase);

    private static readonly Regex ServerVariable = new(@"@@(?<name>\w+)", RegexOptions.IgnoreCase);

    private static readonly Regex TopClause = new(@"^\s*SELECT\s+TOP\s*\(?\s*(?<n>\d+)\s*\)?\s+", RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> ServerVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["version"] = "'Microsoft SQL Server 2019 - 15.0.0 (Baseport)'",
        ["servername"] = "'baseport'",
        ["servicename"] = "'MSSQLSERVER'",
        ["spid"] = "0",
        ["language"] = "'us_english'",
        ["max_precision"] = "38",
        ["microsoftversion"] = "251658240",
        ["rowcount"] = "0",
        ["error"] = "0",
        ["trancount"] = "0",
        ["nestlevel"] = "0",
        ["fetch_status"] = "0",
        ["identity"] = "NULL",
    };

    // sqlite's tokenizer rejects @ outright, @@version cannot be a registered function the way DB_NAME() can; the reference is substituted before the statement is parsed
    private static string RewriteServerVariables(string sql) =>
        sql.Contains("@@", StringComparison.Ordinal)
            ? ServerVariable.Replace(sql, m => ServerVariables.GetValueOrDefault(m.Groups["name"].Value, "NULL"))
            : sql;

    // t-sql caps a result set at the front of the statement and sqlite at the end; without this every browser's "select the first n rows" is a syntax error
    private static string RewriteTop(string sql) =>
        TopClause.Match(sql) is { Success: true } m
            ? $"SELECT {sql[m.Length..].TrimEnd().TrimEnd(';')} LIMIT {m.Groups["n"].Value}"
            : sql;

    // ssms and sqlclient read these to identify the server and pick the database to browse; sqlite has none of them
    private static void RegisterCompatibilityFunctions(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        conn.CreateFunction("db_name", () => "baseport");
        conn.CreateFunction("db_name", (long _) => "baseport");
        conn.CreateFunction("db_id", () => 5L);
        conn.CreateFunction("schema_name", () => "dbo");
        conn.CreateFunction("schema_name", (long _) => "dbo");
        conn.CreateFunction("schema_id", () => 1L);
        conn.CreateFunction("suser_sname", () => "baseport");
        conn.CreateFunction("suser_name", () => "baseport");
        conn.CreateFunction("user_name", () => "dbo");
        conn.CreateFunction("original_login", () => "baseport");
        conn.CreateFunction("host_name", () => "baseport");
        conn.CreateFunction("app_name", () => "Baseport");
        conn.CreateFunction("getdate", () => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        conn.CreateFunction("getutcdate", () => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        conn.CreateFunction("isnull", (object? value, object? fallback) => value ?? fallback);
        conn.CreateFunction("object_id", (string? _) => (object?)null);
        conn.CreateFunction("object_name", (long _) => (object?)null);
        conn.CreateFunction("serverproperty", (string? property) => ServerProperty(property));
        conn.CreateFunction("databasepropertyex", (string? _, string? _) => (object?)null);
    }

    private static string? ServerProperty(string? property) => (property ?? "").ToLowerInvariant() switch
    {
        "productversion" => "15.0.0",
        "productlevel" => "RTM",
        "productmajorversion" => "15",
        "edition" => "Developer Edition (64-bit)",
        "engineedition" => "3",
        "servername" or "machinename" => "baseport",
        "instancename" => null,
        "collation" => "SQL_Latin1_General_CP1_CI_AS",
        "isclustered" or "ishadrenabled" or "isintegratedsecurityonly" => "0",
        _ => null,
    };

    private static async Task RunQueryAsync(NetworkStream stream, IServiceScopeFactory scopes, string sql, string userId, CancellationToken ct)
    {
        if (NoOpStatement.IsMatch(sql))
        {
            using var noop = new MemoryStream();
            WriteDone(noop, 0x0000, 0, 0);
            await WriteTdsMessageAsync(stream, PtTabularResult, noop.ToArray(), ct);
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        sql = RewriteTop(RewriteServerVariables(sql));
        var invalid = SqlEngine.Validate(sql);
        var result = invalid is null
            ? await SqlEngine.ReadAsync(db, sql, conn =>
            {
                RegisterCompatibilityFunctions(conn);
                WireCatalog.Apply(conn, WireDialect.Tds, userId);
            })
            : new SqlEngine.Result([], [], false, invalid);

        // WireCatalog answers the catalog objects a browser reads; an unemulated sys.* object answers empty here instead of returning raw sqlite error text to the client
        if (result.Error is not null && CatalogQuery.IsMatch(sql))
            result = new SqlEngine.Result([""], [], false, null);

        using var ms = new MemoryStream();
        if (result.Error is not null)
        {
            WriteError(ms, result.Error);
            WriteDone(ms, 0x0002, 0xC1, 0); // done_error
        }
        else
        {
            WriteColMetadata(ms, result.Columns);
            foreach (var row in result.Rows) WriteRow(ms, row);
            WriteDone(ms, 0x0010, 0xC1, (ulong)result.Rows.Count); // done_count, curcmd = select
        }
        await WriteTdsMessageAsync(stream, PtTabularResult, ms.ToArray(), ct);
    }

    // sqlbatch payload is, on tds 7.2+, an optional all_headers block (its own total byte length as the first dword) followed by the utf-16le query text
    private static string ExtractBatchText(byte[] payload)
    {
        var start = 0;
        if (payload.Length >= 4)
        {
            var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4));
            if (totalLength >= 4 && totalLength <= payload.Length && (payload.Length - totalLength) % 2 == 0)
                start = (int)totalLength;
        }
        var byteCount = payload.Length - start;
        return byteCount <= 0 ? "" : Encoding.Unicode.GetString(payload, start, byteCount);
    }

    // login7

    private static (string Username, string Password) ParseLogin7(byte[] payload)
    {
        if (payload.Length < 48) return ("", "");
        ushort ReadU16(int off) => BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(off, 2));

        var username = ReadUnicode(payload, ReadU16(40), ReadU16(42));
        var password = DecodePassword(payload, ReadU16(44), ReadU16(46));
        return (username, password);
    }

    private static string ReadUnicode(byte[] buf, int offset, int charCount)
    {
        if (charCount == 0 || offset < 0 || offset + charCount * 2 > buf.Length) return "";
        return Encoding.Unicode.GetString(buf, offset, charCount * 2);
    }

    // tds obfuscates login7 passwords by swapping each byte's nibbles then xoring with 0xa5, decoding undoes it in reverse: xor first, then swap back
    private static string DecodePassword(byte[] buf, int offset, int charCount)
    {
        if (charCount == 0 || offset < 0 || offset + charCount * 2 > buf.Length) return "";
        var bytes = new byte[charCount * 2];
        Array.Copy(buf, offset, bytes, 0, bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            var x = (byte)(bytes[i] ^ 0xA5);
            bytes[i] = (byte)(((x & 0x0F) << 4) | ((x & 0xF0) >> 4));
        }
        return Encoding.Unicode.GetString(bytes);
    }

    // packet framing: header fields are big-endian, everything inside tds payloads is little-endian

    private static async Task<(byte Type, byte[] Payload)?> ReadTdsMessageAsync(NetworkStream stream, CancellationToken ct)
    {
        byte? messageType = null;
        using var buffer = new MemoryStream();
        while (true)
        {
            var header = await stream.ReadExactAsync(8, ct);
            if (header is null) return null;
            var type = header[0];
            var status = header[1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
            if (length < 8) return null;
            var payload = await stream.ReadExactAsync(length - 8, ct);
            if (payload is null) return null;

            messageType ??= type;
            buffer.Write(payload);
            if ((status & 0x01) != 0) break; // eom
        }
        return (messageType!.Value, buffer.ToArray());
    }

    private static async Task WriteTdsMessageAsync(NetworkStream stream, byte type, byte[] payload, CancellationToken ct)
    {
        var offset = 0;
        byte packetId = 1;
        do
        {
            var chunkLen = Math.Min(MaxPacketPayload, payload.Length - offset);
            var isLast = offset + chunkLen >= payload.Length;
            var header = new byte[8];
            header[0] = type;
            header[1] = (byte)(isLast ? 0x01 : 0x00);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)(chunkLen + 8));
            header[6] = packetId++;
            await stream.WriteAsync(header, ct);
            if (chunkLen > 0) await stream.WriteAsync(payload.AsMemory(offset, chunkLen), ct);
            offset += chunkLen;
        } while (offset < payload.Length);
    }

    private static void WriteU16BE(MemoryStream ms, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteU16LE(MemoryStream ms, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteU32LE(MemoryStream ms, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteU32BE(MemoryStream ms, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        ms.Write(buf);
    }

    private static void WriteU64LE(MemoryStream ms, ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        ms.Write(buf);
    }

    // prelogin response

    private static byte[] BuildPreloginResponse()
    {
        (byte Token, byte[] Data)[] options =
        [
            (0x00, [0, 0, 0, 0, 0, 0]), // version
            (0x01, [0x02]),             // encryption: not supported
            (0x02, [0x00]),             // instopt
            (0x03, [0, 0, 0, 0]),       // threadid
            (0x04, [0x00]),             // mars: off
        ];

        var tableSize = options.Length * 5 + 1; // 5 bytes per entry + 1 terminator byte
        using var ms = new MemoryStream();
        var offset = tableSize;
        foreach (var (token, data) in options)
        {
            ms.WriteByte(token);
            WriteU16BE(ms, (ushort)offset);
            WriteU16BE(ms, (ushort)data.Length);
            offset += data.Length;
        }
        ms.WriteByte(0xFF);
        foreach (var (_, data) in options) ms.Write(data);
        return ms.ToArray();
    }

    // response tokens

    private static void WriteLoginAck(MemoryStream ms)
    {
        using var body = new MemoryStream();
        body.WriteByte(0x01); // interface: tds
        WriteU32BE(body, 0x74000004); // tdsversion: 7.4 — this one field is network byte order, unlike the rest of tds
        var progName = Encoding.Unicode.GetBytes("Baseport");
        body.WriteByte((byte)"Baseport".Length);
        body.Write(progName);
        body.WriteByte(0); // majorver
        body.WriteByte(1); // minorver
        body.WriteByte(0); // buildnumhi
        body.WriteByte(0); // buildnumlo

        ms.WriteByte(0xAD); // loginack token
        WriteU16LE(ms, (ushort)body.Length);
        body.WriteTo(ms);
    }

    private static void WriteError(MemoryStream ms, string message)
    {
        using var body = new MemoryStream();
        WriteU32LE(body, 50000); // number: user error range
        body.WriteByte(1);  // state
        body.WriteByte(16); // class (severity)
        var msgBytes = Encoding.Unicode.GetBytes(message);
        WriteU16LE(body, (ushort)message.Length);
        body.Write(msgBytes);
        const string serverName = "Baseport";
        body.WriteByte((byte)serverName.Length);
        body.Write(Encoding.Unicode.GetBytes(serverName));
        body.WriteByte(0); // procname length: none
        WriteU32LE(body, 0); // linenumber

        ms.WriteByte(0xAA); // error token
        WriteU16LE(ms, (ushort)body.Length);
        body.WriteTo(ms);
    }

    private static void WriteDone(MemoryStream ms, ushort status, ushort curCmd, ulong rowCount)
    {
        ms.WriteByte(0xFD); // done token
        WriteU16LE(ms, status);
        WriteU16LE(ms, curCmd);
        WriteU64LE(ms, rowCount);
    }

    private static void WriteColMetadata(MemoryStream ms, List<string> columns)
    {
        ms.WriteByte(0x81); // colmetadata token
        if (columns.Count == 0) { WriteU16LE(ms, 0xFFFF); return; }

        WriteU16LE(ms, (ushort)columns.Count);
        foreach (var col in columns)
        {
            WriteU32LE(ms, 0);      // usertype
            WriteU16LE(ms, 0x0001); // flags: nullable
            ms.WriteByte(0xE7);     // typeid: nvarchartype
            WriteU16LE(ms, 8000);   // maxlength: 4000 chars
            ms.Write((byte[])[0x09, 0x04, 0x00, 0x00, 0x00]); // collation: sql_latin1_general_cp1_ci_as
            var name = col.Length > 128 ? col[..128] : col;
            var nameBytes = Encoding.Unicode.GetBytes(name);
            ms.WriteByte((byte)name.Length);
            ms.Write(nameBytes);
        }
    }

    private static void WriteRow(MemoryStream ms, List<string?> row)
    {
        ms.WriteByte(0xD1); // row token
        foreach (var value in row)
        {
            if (value is null) { WriteU16LE(ms, 0xFFFF); continue; }
            var text = value.Length > 4000 ? value[..4000] : value;
            var bytes = Encoding.Unicode.GetBytes(text);
            WriteU16LE(ms, (ushort)bytes.Length);
            ms.Write(bytes);
        }
    }
}
