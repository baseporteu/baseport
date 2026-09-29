using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class ConnectionsTests : IDisposable
{
    private const string PublicUrl = "https://93.184.216.34/api";
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public ConnectionsTests()
    {
        TestSecrets.Ensure();
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<string> SecretAsync(string name, string value)
    {
        var s = new Secret { Id = Ids.NewShortId(12), Name = name, ValueProtected = Secrets.Protect(value), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _db.Secrets.Add(s);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return s.Id;
    }

    private static Connection Conn(Action<Connection>? change = null)
    {
        var c = new Connection { Id = "c1", Name = "crm", BaseUrl = PublicUrl };
        change?.Invoke(c);
        return c;
    }

    private static string Headers(params Connections.HeaderSpec[] headers) => Connections.SerializeHeaders(headers);

    private Task<List<string>> Problems(Connection c) => Connections.ProblemsAsync(_db, c, TestContext.Current.CancellationToken);

    [Fact]
    public async Task PlainConnectionIsValid()
    {
        Assert.Empty(await Problems(Conn()));
    }

    [Theory]
    [InlineData("ftp://93.184.216.34/")]
    [InlineData("https://user:pass@93.184.216.34/")]
    [InlineData("https://93.184.216.34/#frag")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("not a url")]
    public async Task UnsafeBaseUrlsAreRefused(string url)
    {
        Assert.NotEmpty(await Problems(Conn(c => c.BaseUrl = url)));
    }

    [Theory]
    [InlineData("bearer")]
    [InlineData("basic")]
    [InlineData("header")]
    public async Task AuthNeedsAnExistingSecret(string kind)
    {
        var c = Conn(c => { c.AuthKind = kind; c.AuthHeaderName = "X-Api-Key"; c.BasicUsername = "svc"; c.AuthSecretId = "missing"; });

        Assert.Contains(await Problems(c), e => e.Contains("secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("X Bad")]
    [InlineData("X-Bad:\r\nInjected")]
    public async Task ReservedOrInvalidHeaderNamesAreRefused(string name)
    {
        Assert.NotEmpty(await Problems(Conn(c => c.HeadersJson = Headers(new Connections.HeaderSpec(name, "v")))));
    }

    [Theory]
    [InlineData("a\r\nX-Injected: 1")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    public async Task HeaderValuesCannotBreakLines(string value)
    {
        Assert.NotEmpty(await Problems(Conn(c => c.HeadersJson = Headers(new Connections.HeaderSpec("X-Tenant", value)))));
    }

    [Fact]
    public async Task HeaderNeedsExactlyOneSource()
    {
        var id = await SecretAsync("tenant", "t1");

        Assert.NotEmpty(await Problems(Conn(c => c.HeadersJson = Headers(new Connections.HeaderSpec("X-Tenant")))));
        Assert.NotEmpty(await Problems(Conn(c => c.HeadersJson = Headers(new Connections.HeaderSpec("X-Tenant", "v", id)))));
        Assert.Empty(await Problems(Conn(c => c.HeadersJson = Headers(new Connections.HeaderSpec("X-Tenant", SecretId: id)))));
    }

    [Fact]
    public async Task DuplicateHeadersAreRefused()
    {
        Assert.NotEmpty(await Problems(Conn(c => c.HeadersJson = Headers(new Connections.HeaderSpec("X-A", "1"), new Connections.HeaderSpec("x-a", "2")))));
    }

    [Fact]
    public async Task NoneClearsCredentialFields()
    {
        var c = Conn(c => { c.AuthKind = "none"; c.AuthSecretId = "x"; c.BasicUsername = "u"; c.AuthHeaderName = "X-K"; });

        Assert.Empty(await Problems(c));
        Assert.Equal("", c.AuthSecretId);
        Assert.Equal("", c.BasicUsername);
        Assert.Equal("", c.AuthHeaderName);
    }

    [Fact]
    public async Task BearerRequest()
    {
        var id = await SecretAsync("tok", "bearer-plain");
        var c = Conn(c => { c.AuthKind = "bearer"; c.AuthSecretId = id; });

        using var req = await Connections.RequestAsync(_db, c, Connections.Resolve(c, "items?page=2"), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("bearer-plain", req.Headers.Authorization.Parameter);
        Assert.Equal("https://93.184.216.34/api/items?page=2", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task BasicRequest()
    {
        var id = await SecretAsync("pw", "p@ss");
        var c = Conn(c => { c.AuthKind = "basic"; c.BasicUsername = "svc"; c.AuthSecretId = id; });

        using var req = await Connections.RequestAsync(_db, c, Connections.Resolve(c, "x"), TestContext.Current.CancellationToken);

        Assert.Equal("svc:p@ss", Encoding.UTF8.GetString(Convert.FromBase64String(req.Headers.Authorization!.Parameter!)));
    }

    [Fact]
    public async Task HeaderAuthAndSecretHeaders()
    {
        var key = await SecretAsync("key", "k-123");
        var tenant = await SecretAsync("tenant", "t-9");
        var c = Conn(c =>
        {
            c.AuthKind = "header";
            c.AuthHeaderName = "X-Api-Key";
            c.AuthSecretId = key;
            c.HeadersJson = Headers(new Connections.HeaderSpec("X-Tenant", SecretId: tenant), new Connections.HeaderSpec("X-Version", "2"));
        });

        using var req = await Connections.RequestAsync(_db, c, Connections.Resolve(c, "x"), TestContext.Current.CancellationToken);

        Assert.Equal("k-123", req.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("t-9", req.Headers.GetValues("X-Tenant").Single());
        Assert.Equal("2", req.Headers.GetValues("X-Version").Single());
        Assert.Null(req.Headers.Authorization);
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("http://93.184.216.34/api")]
    [InlineData("https://93.184.216.34:8443/api")]
    public async Task CredentialsNeverLeaveTheOrigin(string url)
    {
        var id = await SecretAsync("tok", "bearer-plain");
        var c = Conn(c => { c.AuthKind = "bearer"; c.AuthSecretId = id; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => Connections.RequestAsync(_db, c, new Uri(url), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DtoCarriesSecretIdsNotValues()
    {
        var id = await SecretAsync("tok", "bearer-plain");
        var c = Conn(c => { c.AuthKind = "bearer"; c.AuthSecretId = id; c.HeadersJson = Headers(new Connections.HeaderSpec("X-T", SecretId: id)); });

        var json = JsonSerializer.Serialize(Connections.Dto(c));

        Assert.DoesNotContain("bearer-plain", json);
        Assert.Contains(id, json);
    }

    [Fact]
    public async Task ReferencedSecretsAreReported()
    {
        var id = await SecretAsync("tok", "v");
        _db.Connections.Add(Conn(c => { c.HeadersJson = Headers(new Connections.HeaderSpec("X-T", SecretId: id)); }));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Contains("connection crm", await SecretStore.ReferencesAsync(_db, id, TestContext.Current.CancellationToken));
    }
}
