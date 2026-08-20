using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Baseport.Providers.Postgres;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Baseport.Tests;

// speaks the postgres wire protocol (v3) over a real loopback socket against the actual PostgresConnection handler: regression coverage for handshake/auth/error paths, parity coverage that a wire query matches SqlEngine.ReadAsync run directly
public class PostgresProviderTests : IAsyncLifetime
{
    private const string Token = "wire-test-token";
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
            db.Tables.Add(new TableDefinition { Id = Ids.NewShortId(12), Name = "Orders" });
            db.UserAccounts.Add(new UserAccount
            {
                Id = Ids.NewShortId(12),
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
                _ = PostgresConnection.HandleAsync(socket, scopes, _cts.Token);
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
        using var client = await ConnectAsync();
        var (columns, rows) = await RunQueryAsync(client, "SELECT Name FROM _tables");

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var direct = await SqlEngine.ReadAsync(db, "SELECT Name FROM _tables");

        Assert.Equal(direct.Columns, columns);
        Assert.Equal(direct.Rows, rows);
        Assert.Equal("Orders", Assert.Single(Assert.Single(rows)));
    }

    [Fact]
    public async Task A_wrong_token_is_refused_at_the_password_message()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        await SendStartupAsync(stream);
        var auth = await ReadMessageAsync(stream);
        Assert.Equal('R', auth!.Value.Type);

        await SendPasswordAsync(stream, "not-the-token");
        var reply = await ReadMessageAsync(stream);
        Assert.Equal('E', reply!.Value.Type);
        client.Dispose();
    }

    [Fact]
    public async Task A_broken_query_reports_an_error_and_the_connection_stays_usable()
    {
        using var client = await ConnectAsync();

        var stream = client.GetStream();
        await SendQueryAsync(stream, "SELECT NoSuchColumn FROM _tables");
        var errorMsg = await ReadMessageAsync(stream);
        Assert.Equal('E', errorMsg!.Value.Type);
        var ready = await ReadMessageAsync(stream);
        Assert.Equal('Z', ready!.Value.Type);

        // the same connection must still answer a subsequent, valid query
        var (columns, rows) = await RunQueryAsync(client, "SELECT Name FROM _tables");
        Assert.Equal(new[] { "Name" }, columns);
        Assert.Single(rows);
    }

    // real drivers (psycopg2, jdbc, dbeaver) send these to configure the session on connect; they must not trip the read-only allowlist
    [Theory]
    [InlineData("SET extra_float_digits = 3")]
    [InlineData("SET application_name = 'psql'")]
    [InlineData("RESET ALL")]
    [InlineData("begin")]
    [InlineData("commit")]
    public async Task A_session_configuration_statement_is_a_no_op_not_an_error(string sql)
    {
        using var client = await ConnectAsync();
        var stream = client.GetStream();
        await SendQueryAsync(stream, sql);

        var reply = await ReadMessageAsync(stream);
        Assert.Equal('C', reply!.Value.Type); // commandcomplete, not an errorresponse
        Assert.Equal('Z', (await ReadMessageAsync(stream))!.Value.Type);
    }

    // the author's tables are rows in _tables, not sqlite tables, so a browser only finds them if the catalog reports them
    [Theory]
    [InlineData("SELECT table_name FROM information_schema.tables")]
    [InlineData("SELECT relname FROM pg_catalog.pg_class WHERE relkind = 'r'")]
    [InlineData("SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname = 'public'")]
    public async Task A_catalog_probe_finds_the_authors_table(string sql)
    {
        using var client = await ConnectAsync();
        var (_, rows) = await RunQueryAsync(client, sql);
        Assert.Equal("Orders", Assert.Single(Assert.Single(rows)));
    }

    // every record is a json blob in one shared table, so these are the columns a client is told about and the ones it can then select
    [Fact]
    public async Task A_column_probe_reports_the_tables_fields()
    {
        using var client = await ConnectAsync();
        var (_, rows) = await RunQueryAsync(client,
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'Orders' ORDER BY ordinal_position");
        Assert.Equal(new[] { "id", "created_at", "updated_at" }, rows.Select(r => r[0]));
    }

    // dbeaver/jdbc probe objects the catalog does not emulate; those must still answer empty rather than aborting the connection with a raw "SQLite Error 1: no such table"
    [Theory]
    [InlineData("SELECT * FROM pg_stat_activity")]
    [InlineData("SELECT * FROM pg_catalog.pg_largeobject")]
    public async Task An_unemulated_catalog_object_still_answers_empty(string sql)
    {
        using var client = await ConnectAsync();
        var (columns, rows) = await RunQueryAsync(client, sql);
        Assert.Single(columns);
        Assert.Empty(rows);
    }

    // dbeaver reads these three to identify the server and pick the schema to browse; sqlite has none of them, and a client with no current database has no node to hang a tree under
    [Theory]
    [InlineData("SELECT version()", "version", "PostgreSQL 15.0 (Baseport)")]
    [InlineData("SELECT current_schema()", "current_schema", "public")]
    [InlineData("SELECT current_database()", "current_database", "baseport")]
    public async Task A_server_identity_function_answers_like_postgres(string sql, string column, string value)
    {
        using var client = await ConnectAsync();
        var (columns, rows) = await RunQueryAsync(client, sql);
        Assert.Equal(column, Assert.Single(columns));
        Assert.Equal(value, Assert.Single(Assert.Single(rows)));
    }

    // pgjdbc sends these during connection setup: SHOW is neither a no-op nor on SqlEngine's allowlist, so it used to abort the connect with an error
    [Theory]
    [InlineData("SHOW search_path", "search_path", "public")]
    [InlineData("SHOW TRANSACTION ISOLATION LEVEL", "transaction_isolation", "read committed")]
    [InlineData("SHOW nonsense_setting", "nonsense_setting", "")]
    public async Task A_show_statement_answers_instead_of_erroring(string sql, string column, string value)
    {
        using var client = await ConnectAsync();
        var (columns, rows) = await RunQueryAsync(client, sql);
        Assert.Equal(column, Assert.Single(columns));
        Assert.Equal(value, Assert.Single(Assert.Single(rows)));
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task<TcpClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        await SendStartupAsync(stream);
        Assert.Equal('R', (await ReadMessageAsync(stream))!.Value.Type); // authtype 3: cleartext password

        await SendPasswordAsync(stream, Token);
        Assert.Equal('R', (await ReadMessageAsync(stream))!.Value.Type); // authtype 0: ok
        while (true)
        {
            var msg = (await ReadMessageAsync(stream))!.Value;
            if (msg.Type == 'Z') break; // readyforquery: startup sequence complete
        }
        return client;
    }

    private static async Task<(List<string> Columns, List<List<string?>> Rows)> RunQueryAsync(TcpClient client, string sql)
    {
        var stream = client.GetStream();
        await SendQueryAsync(stream, sql);

        var rowDesc = (await ReadMessageAsync(stream))!.Value;
        Assert.Equal('T', rowDesc.Type);
        var i = 0;
        var count = ReadI16(rowDesc.Payload, ref i);
        var columns = new List<string>();
        for (var c = 0; c < count; c++)
        {
            columns.Add(ReadCString(rowDesc.Payload, ref i));
            i += 4 + 2 + 4 + 2 + 4 + 2; // table oid, attnum, type oid, type size, type modifier, format code
        }

        var rows = new List<List<string?>>();
        while (true)
        {
            var msg = (await ReadMessageAsync(stream))!.Value;
            if (msg.Type == 'C')
            {
                Assert.Equal('Z', (await ReadMessageAsync(stream))!.Value.Type);
                break;
            }
            Assert.Equal('D', msg.Type);
            var j = 0;
            var fieldCount = ReadI16(msg.Payload, ref j);
            var row = new List<string?>();
            for (var f = 0; f < fieldCount; f++)
            {
                var len = BinaryPrimitives.ReadInt32BigEndian(msg.Payload.AsSpan(j, 4));
                j += 4;
                if (len < 0) { row.Add(null); continue; }
                row.Add(Encoding.UTF8.GetString(msg.Payload, j, len));
                j += len;
            }
            rows.Add(row);
        }
        return (columns, rows);
    }

    // --- Minimal client-side wire encoding ---

    private static async Task SendStartupAsync(NetworkStream stream)
    {
        using var ms = new MemoryStream();
        WriteI32(ms, 0);
        WriteI32(ms, 196608); // protocol version 3.0
        WriteCString(ms, "user"); WriteCString(ms, "wire-test");
        WriteCString(ms, "database"); WriteCString(ms, "baseport");
        ms.WriteByte(0);
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), bytes.Length);
        await stream.WriteAsync(bytes);
    }

    private static Task SendPasswordAsync(NetworkStream stream, string password)
    {
        using var ms = new MemoryStream();
        WriteCString(ms, password);
        return WriteMessageAsync(stream, (byte)'p', ms.ToArray());
    }

    private static Task SendQueryAsync(NetworkStream stream, string sql)
    {
        using var ms = new MemoryStream();
        WriteCString(ms, sql);
        return WriteMessageAsync(stream, (byte)'Q', ms.ToArray());
    }

    private static async Task WriteMessageAsync(NetworkStream stream, byte type, byte[] payload)
    {
        var header = new byte[5];
        header[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length + 4);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
    }

    private static async Task<(char Type, byte[] Payload)?> ReadMessageAsync(NetworkStream stream)
    {
        var typeBuf = await ReadExactAsync(stream, 1);
        if (typeBuf is null) return null;
        var lenBuf = await ReadExactAsync(stream, 4);
        if (lenBuf is null) return null;
        var length = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        var payload = await ReadExactAsync(stream, length - 4) ?? Array.Empty<byte>();
        return ((char)typeBuf[0], payload);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int count)
    {
        if (count <= 0) return Array.Empty<byte>();
        var buf = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buf.AsMemory(offset, count - offset));
            if (read == 0) return null;
            offset += read;
        }
        return buf;
    }

    private static void WriteCString(MemoryStream ms, string s)
    {
        ms.Write(Encoding.UTF8.GetBytes(s));
        ms.WriteByte(0);
    }

    private static void WriteI32(MemoryStream ms, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, value);
        ms.Write(buf);
    }

    private static string ReadCString(byte[] buf, ref int i)
    {
        var start = i;
        while (buf[i] != 0) i++;
        var s = Encoding.UTF8.GetString(buf, start, i - start);
        i++;
        return s;
    }

    private static short ReadI16(byte[] buf, ref int i)
    {
        var v = BinaryPrimitives.ReadInt16BigEndian(buf.AsSpan(i, 2));
        i += 2;
        return v;
    }
}
