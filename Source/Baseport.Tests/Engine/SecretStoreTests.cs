using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class SecretStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public SecretStoreTests()
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

    private async Task<Secret> AddAsync(string name, string value)
    {
        var now = DateTime.UtcNow;
        var secret = new Secret { Id = Ids.NewShortId(12), Name = name, ValueProtected = Secrets.Protect(value), CreatedAt = now, UpdatedAt = now };
        _db.Secrets.Add(secret);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return secret;
    }

    [Theory]
    [InlineData("crm-token", true)]
    [InlineData("a1", true)]
    [InlineData("A-token", false)]
    [InlineData("1token", false)]
    [InlineData("token_name", false)]
    [InlineData("x", false)]
    [InlineData("", false)]
    public void NameRules(string name, bool valid)
    {
        Assert.Equal(valid, SecretStore.NameProblems(name).Count == 0);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("v", true)]
    public void ValueRules(string? value, bool valid)
    {
        Assert.Equal(valid, SecretStore.ValueProblem(value) is null);
        Assert.NotNull(SecretStore.ValueProblem(new string('x', SecretStore.MaxValueLength + 1)));
    }

    [Fact]
    public async Task StoredOnlyEncrypted()
    {
        var secret = await AddAsync("crm-token", "hunter2-plain");

        var raw = await _db.Database.SqlQueryRaw<string>("SELECT \"ValueProtected\" AS \"Value\" FROM \"_secrets\"").ToListAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("hunter2", raw.Single());
        Assert.NotEqual("hunter2-plain", secret.ValueProtected);
    }

    [Fact]
    public async Task ResolveDecryptsAndStampsUse()
    {
        var secret = await AddAsync("crm-token", "hunter2-plain");

        var value = await SecretStore.ResolveAsync(_db, secret.Id, TestContext.Current.CancellationToken);

        Assert.Equal("hunter2-plain", value);
        _db.ChangeTracker.Clear();
        Assert.NotNull((await _db.Secrets.SingleAsync(TestContext.Current.CancellationToken)).LastUsedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    public async Task ResolveUnknownIsNull(string? id)
    {
        Assert.Null(await SecretStore.ResolveAsync(_db, id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DtoNeverCarriesTheValue()
    {
        var secret = await AddAsync("crm-token", "hunter2-plain");

        var json = JsonSerializer.Serialize(SecretStore.Dto(secret));

        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain(secret.ValueProtected, json);
        Assert.DoesNotContain("value", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProxyTokenStoredEncrypted()
    {
        var table = new TableDefinition { Id = Ids.NewShortId(12), Name = "Remote", IsProxy = true, ProxyTokenProtected = Secrets.Protect("bearer-plain") };
        _db.Tables.Add(table);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var raw = await _db.Database.SqlQueryRaw<string>("SELECT \"ProxyTokenProtected\" AS \"Value\" FROM \"_tables\"").ToListAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("bearer-plain", raw.Single());
        Assert.Equal("bearer-plain", Secrets.Unprotect(raw.Single()));
        Assert.DoesNotContain("bearer-plain", JsonSerializer.Serialize(ApiDtos.TableDto(table)));
    }
}
