using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Baseport.Providers.Tds;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Baseport.Tests;

// speaks tds (sql server wire protocol) over a real loopback socket against the actual TdsConnection handler: regression coverage for handshake/auth/error paths, parity coverage that a wire query matches SqlEngine.ReadAsync run directly
public class TdsProviderTests : IAsyncLifetime
{
    private const string Token = "wire-test-token";
    private string _accountId = "";
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;
    private TcpListener _listener = null!;
    private Task _acceptLoop = null!;
    private CancellationTokenSource _cts = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        _services = services.BuildServiceProvider();

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tables.Add(new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders", ApiEnabled = true, ApiName = "orders" });
            _accountId = Ids.NewShortId(12);
            db.UserAccounts.Add(new UserAccount
            {
                Id = _accountId,
                Username = "wire-test",
                Email = "wire@test.local",
                ApiEnabled = true,
                ApiTokenHash = ApiAuth.HashToken(Token),
            });
            await db.SaveChangesAsync();
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        try
        {
            while (true)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                _ = TdsConnection.HandleAsync(socket, scopes, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _acceptLoop; } catch { /* cancellation */ }
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        _cts.Dispose();
    }

    [Fact]
    public async Task A_query_over_the_wire_matches_SqlEngine_run_directly()
    {
        const string sql = "SELECT name FROM sys.tables";
        using var client = await ConnectAsync(Token);
        var (columns, rows) = await RunBatchAsync(client, sql);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var direct = await SqlEngine.ReadAsync(db, sql, conn => WireCatalog.Apply(conn, WireDialect.Tds, _accountId));

        Assert.Equal(direct.Columns, columns);
        Assert.Equal(direct.Rows, rows);
        Assert.Equal("Orders", Assert.Single(Assert.Single(rows)));
    }

    // The wire is an api-token surface, it must reach only the projected author tables, never the storage schema behind them: _users stores password hashes and _settings the jwt signing key. A direct read of the main schema is refused by the connection authorizer.
    [Theory]
    [InlineData("SELECT authsigningkey FROM _settings")]
    [InlineData("SELECT passwordhash FROM _users")]
    [InlineData("SELECT * FROM _records")]
    [InlineData("SELECT name FROM sqlite_master")]
    public async Task A_read_of_a_system_table_is_refused(string sql)
    {
        using var client = await ConnectAsync(Token);
        var stream = client.GetStream();
        await WriteTdsMessageAsync(stream, 0x01, Encoding.Unicode.GetBytes(sql));
        var (_, payload) = await ReadTdsMessageAsync(stream);
        Assert.Equal(0xAA, payload[0]); // error token
    }

    [Fact]
    public async Task A_wrong_token_is_refused_at_login()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port, TestContext.Current.CancellationToken);
        var stream = client.GetStream();

        await WriteTdsMessageAsync(stream, 0x12, [0x00]); // prelogin: content is ignored by the server
        await ReadTdsMessageAsync(stream); // prelogin response

        await WriteTdsMessageAsync(stream, 0x10, BuildLogin7("wire-test", "not-the-token"));
        var (_, payload) = await ReadTdsMessageAsync(stream);
        Assert.Equal(0xAA, payload[0]); // error token, not loginack (0xad)
    }

    [Fact]
    public async Task A_broken_query_reports_an_error_and_the_connection_stays_usable()
    {
        using var client = await ConnectAsync(Token);
        var stream = client.GetStream();

        await WriteTdsMessageAsync(stream, 0x01, Encoding.Unicode.GetBytes("SELECT NoSuchColumn FROM Orders"));
        var (_, errorPayload) = await ReadTdsMessageAsync(stream);
        Assert.Equal(0xAA, errorPayload[0]); // error token

        // the same connection must still answer a subsequent, valid batch
        var (columns, rows) = await RunBatchAsync(client, "SELECT name FROM sys.tables");
        Assert.Equal(["name"], columns);
        Assert.Single(rows);
    }

    // the author's tables are rows in _tables, not sqlite tables, a browser only finds them if the catalog reports them
    [Theory]
    [InlineData("SELECT name FROM sys.tables")]
    [InlineData("SELECT name FROM sys.objects WHERE type = 'U '")]
    [InlineData("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES")]
    public async Task A_catalog_probe_finds_the_authors_table(string sql)
    {
        using var client = await ConnectAsync(Token);
        var (_, rows) = await RunBatchAsync(client, sql);
        Assert.Equal("Orders", Assert.Single(Assert.Single(rows)));
    }

    // sqlite's tokenizer rejects @, these are substituted before the statement is parsed instead of registered as functions
    [Theory]
    [InlineData("SELECT @@version", "Microsoft SQL Server 2019 - 15.0.0 (Baseport)")]
    [InlineData("SELECT @@SERVERNAME", "baseport")]
    [InlineData("SELECT DB_NAME()", "baseport")]
    [InlineData("SELECT SCHEMA_NAME()", "dbo")]
    [InlineData("SELECT SERVERPROPERTY('ProductVersion')", "15.0.0")]
    public async Task A_server_identity_probe_answers_like_sql_server(string sql, string expected)
    {
        using var client = await ConnectAsync(Token);
        var (_, rows) = await RunBatchAsync(client, sql);
        Assert.Equal(expected, Assert.Single(Assert.Single(rows)));
    }

    // t-sql caps the row count at the front of the statement, sqlite at the end
    [Theory]
    [InlineData("SELECT TOP 10 name FROM sys.tables")]
    [InlineData("SELECT TOP (10) name FROM sys.tables")]
    [InlineData("select top 10 name from sys.tables;")]
    public async Task A_top_clause_caps_the_result_instead_of_erroring(string sql)
    {
        using var client = await ConnectAsync(Token);
        var (columns, rows) = await RunBatchAsync(client, sql);
        Assert.Equal(["name"], columns);
        Assert.Equal("Orders", Assert.Single(Assert.Single(rows)));
    }

    // an object the catalog does not emulate must answer empty; it used to hand the client raw "SQLite Error 1: no such table" text
    [Fact]
    public async Task An_unemulated_catalog_object_answers_empty_instead_of_leaking_sqlite()
    {
        using var client = await ConnectAsync(Token);
        var (_, rows) = await RunBatchAsync(client, "SELECT * FROM sys.dm_os_wait_stats");
        Assert.Empty(rows);
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    // Same two rules the postgres listener answers to: the wire shows the tables a token reaches over rest, and only the rows the read rule allows it.
    [Fact]
    public async Task An_unpublished_table_is_neither_listed_nor_selectable()
    {
        await SeedTableAsync("Drafts", apiEnabled: false, readRule: "");

        using var client = await ConnectAsync(Token);
        var (_, rows) = await RunBatchAsync(client, "SELECT name FROM sys.tables");
        Assert.DoesNotContain("Drafts", rows.Select(r => r[0]));

        var stream = client.GetStream();
        await WriteTdsMessageAsync(stream, 0x01, Encoding.Unicode.GetBytes("SELECT * FROM Drafts"));
        var (_, payload) = await ReadTdsMessageAsync(stream);
        Assert.Equal(0xAA, payload[0]); // error token
    }

    [Fact]
    public async Task A_read_rule_filters_the_rows_to_the_caller()
    {
        var tableId = await SeedTableAsync("Tickets", apiEnabled: true, readRule: "_ROW_.owner = _USER_.id", "owner", "subject");
        await SeedRecordAsync(tableId, $$"""{"owner":"{{_accountId}}","subject":"mine"}""");
        await SeedRecordAsync(tableId, """{"owner":"somebody-else","subject":"theirs"}""");

        using var client = await ConnectAsync(Token);
        var (_, rows) = await RunBatchAsync(client, "SELECT subject FROM Tickets");
        Assert.Equal("mine", Assert.Single(Assert.Single(rows)));
    }

    private async Task<string> SeedTableAsync(string name, bool apiEnabled, string readRule, params string[] fields)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Ids.NewShortId(12);
        db.Tables.Add(new TableDefinition { Id = id, Name = name, ApiEnabled = apiEnabled, ApiName = name.ToLowerInvariant(), ReadRule = readRule });
        foreach (var (field, position) in fields.Select((f, i) => (f, i)))
            db.Fields.Add(new FieldDefinition { Id = Ids.NewShortId(12), TableId = id, Name = field, Position = position });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task SeedRecordAsync(string tableId, string json)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Records.Add(new Record { Id = Ids.NewShortId(12), TableId = tableId, JsonData = json, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<TcpClient> ConnectAsync(string token)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port, TestContext.Current.CancellationToken);
        var stream = client.GetStream();

        await WriteTdsMessageAsync(stream, 0x12, [0x00]); // prelogin
        await ReadTdsMessageAsync(stream); // prelogin response

        await WriteTdsMessageAsync(stream, 0x10, BuildLogin7("wire-test", token));
        var (_, loginReply) = await ReadTdsMessageAsync(stream);
        Assert.Equal(0xAD, loginReply[0]); // loginack token: auth succeeded
        return client;
    }

    private static async Task<(List<string> Columns, List<List<string?>> Rows)> RunBatchAsync(TcpClient client, string sql)
    {
        var stream = client.GetStream();
        await WriteTdsMessageAsync(stream, 0x01, Encoding.Unicode.GetBytes(sql));
        var (_, payload) = await ReadTdsMessageAsync(stream);

        var i = 0;
        Assert.Equal(0x81, payload[i++]); // colmetadata token
        var columnCount = ReadU16LE(payload, ref i);
        var columns = new List<string>();
        for (var c = 0; c < columnCount; c++)
        {
            i += 4 + 2 + 1 + 2 + 5; // usertype, flags, typeid, maxlength, collation
            var nameLen = payload[i++];
            columns.Add(Encoding.Unicode.GetString(payload, i, nameLen * 2));
            i += nameLen * 2;
        }

        var rows = new List<List<string?>>();
        while (payload[i] == 0xD1) // row token
        {
            i++;
            var row = new List<string?>();
            for (var c = 0; c < columnCount; c++)
            {
                var len = ReadU16LE(payload, ref i);
                if (len == 0xFFFF) { row.Add(null); continue; }
                row.Add(Encoding.Unicode.GetString(payload, i, len));
                i += len;
            }
            rows.Add(row);
        }
        Assert.Equal(0xFD, payload[i]); // done token closes the batch
        return (columns, rows);
    }

    // --- minimal client-side tds encoding ---

    private static byte[] BuildLogin7(string username, string password)
    {
        var usernameBytes = Encoding.Unicode.GetBytes(username);
        var passwordBytes = EncodePassword(password);
        var fixedLen = 94;
        var body = new byte[fixedLen + usernameBytes.Length + passwordBytes.Length];

        void WriteU16(int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(off, 2), v);
        WriteU16(40, (ushort)fixedLen); WriteU16(42, (ushort)username.Length);
        WriteU16(44, (ushort)(fixedLen + usernameBytes.Length)); WriteU16(46, (ushort)password.Length);
        usernameBytes.CopyTo(body, fixedLen);
        passwordBytes.CopyTo(body, fixedLen + usernameBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), (uint)body.Length);
        return body;
    }

    // tds's login7 scramble: swap each byte's nibbles, then xor with 0xa5
    private static byte[] EncodePassword(string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        for (var i = 0; i < bytes.Length; i++)
        {
            var swapped = (byte)(((bytes[i] & 0x0F) << 4) | ((bytes[i] & 0xF0) >> 4));
            bytes[i] = (byte)(swapped ^ 0xA5);
        }
        return bytes;
    }

    private static async Task WriteTdsMessageAsync(NetworkStream stream, byte type, byte[] payload)
    {
        var header = new byte[8];
        header[0] = type;
        header[1] = 0x01; // eom: the tests never send more than one packet's worth
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)(payload.Length + 8));
        header[6] = 1;
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
    }

    private static async Task<(byte Type, byte[] Payload)> ReadTdsMessageAsync(NetworkStream stream)
    {
        var header = await ReadExactAsync(stream, 8);
        var type = header[0];
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        var payload = await ReadExactAsync(stream, length - 8);
        return (type, payload);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        var buf = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buf.AsMemory(offset, count - offset));
            if (read == 0) throw new IOException("connection closed");
            offset += read;
        }
        return buf;
    }

    private static ushort ReadU16LE(byte[] buf, ref int i)
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i, 2));
        i += 2;
        return v;
    }
}
