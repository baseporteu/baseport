using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

[CollectionDefinition(nameof(UserAuthTests), DisableParallelization = true)]
public class UserAuthCollection;

// mutates the shared token issuer and key
[Collection(nameof(UserAuthTests))]
public class UserAuthTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public UserAuthTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        UserTokens.Initialize(null);
        UserTokens.Configure(new AppSettings());
    }

    private static UserAccount Jane() => new()
    {
        Id = "user00000001",
        Username = "jane",
        Email = "jane@example.com",
        Role = AccountRoles.User
    };

    [Fact]
    public void MintedTokenVerifies()
    {
        var now = DateTime.UtcNow;
        var claims = UserTokens.Verify(UserTokens.Mint(Jane(), "session00001", now), now);

        Assert.NotNull(claims);
        Assert.Equal("user00000001", claims!.Sub);
        Assert.Equal("jane@example.com", claims.Email);
        Assert.Equal("jane", claims.Username);
        Assert.Equal(AccountRoles.User, claims.Role);
    }

    [Fact]
    public void TamperedPayloadRefused()
    {
        var now = DateTime.UtcNow;
        var parts = UserTokens.Mint(Jane(), "session00001", now).Split('.');
        var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"iss":"baseport","aud":"baseport","sub":"admin","iat":0,"exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(UserTokens.Verify($"{parts[0]}.{forged}.{parts[2]}", now));
    }

    [Fact]
    public void ExpiredTokenRefused()
    {
        var issued = DateTime.UtcNow.AddDays(-1);
        Assert.Null(UserTokens.Verify(UserTokens.Mint(Jane(), "session00001", issued), DateTime.UtcNow));
    }

    [Fact]
    public void IssuerChangeRejectsOldTokens()
    {
        var now = DateTime.UtcNow;
        var token = UserTokens.Mint(Jane(), "session00001", now);

        UserTokens.Configure(new AppSettings { AuthIssuer = "acme" });
        Assert.Null(UserTokens.Verify(token, now));

        UserTokens.Configure(new AppSettings());
        Assert.NotNull(UserTokens.Verify(token, now));
    }

    [Fact]
    public void KeyRotationRejectsOldTokens()
    {
        var now = DateTime.UtcNow;
        var token = UserTokens.Mint(Jane(), "session00001", now);

        UserTokens.Rotate();
        Assert.Null(UserTokens.Verify(token, now));
    }

    [Fact]
    public void LifetimeIsClamped()
    {
        UserTokens.Configure(new AppSettings { AuthTokenLifetimeSec = 5, AuthRefreshLifetimeDays = 5000 });
        Assert.Equal(UserTokens.MinTokenLifetimeSec, (int)UserTokens.AuthTokenLifetime.TotalSeconds);
        Assert.Equal(UserTokens.MaxRefreshLifetimeDays, (int)UserTokens.RefreshTokenLifetime.TotalDays);
        UserTokens.Configure(new AppSettings());
    }

    [Fact]
    public async Task RefreshTokenNotRotated()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var user = Jane();
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var issued = await UserTokens.IssueAsync(_db, user, DateTime.UtcNow);
        var again = await UserTokens.ReauthAsync(_db, issued.RefreshToken, DateTime.UtcNow);

        Assert.NotNull(again);
        Assert.Equal(issued.RefreshToken, again!.Value.Tokens.RefreshToken);
        Assert.NotNull(await UserTokens.ReauthAsync(_db, issued.RefreshToken, DateTime.UtcNow));
    }

    [Fact]
    public async Task ExpiredSessionIsPruned()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var user = Jane();
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var past = DateTime.UtcNow.AddDays(-UserTokens.MaxRefreshLifetimeDays - 1);
        var issued = await UserTokens.IssueAsync(_db, user, past);

        Assert.Null(await UserTokens.ReauthAsync(_db, issued.RefreshToken, DateTime.UtcNow));
        Assert.Equal(1, await UserTokens.PruneExpiredAsync(_db, DateTime.UtcNow));
    }

    [Fact]
    public async Task DisabledAccountCannotRefresh()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var user = Jane();
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var issued = await UserTokens.IssueAsync(_db, user, DateTime.UtcNow);
        user.IsDisabled = true;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(await UserTokens.ReauthAsync(_db, issued.RefreshToken, DateTime.UtcNow));
    }

    [Fact]
    public async Task RevokeAllKillsRefreshTokens()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var user = Jane();
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = await UserTokens.IssueAsync(_db, user, DateTime.UtcNow);
        var second = await UserTokens.IssueAsync(_db, user, DateTime.UtcNow);
        await UserTokens.RevokeAllAsync(_db, user.Id);

        Assert.Null(await UserTokens.ReauthAsync(_db, first.RefreshToken, DateTime.UtcNow));
        Assert.Null(await UserTokens.ReauthAsync(_db, second.RefreshToken, DateTime.UtcNow));
    }

    [Fact]
    public async Task RevokeKillsAccessToken()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var user = Jane();
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var issued = await UserTokens.IssueAsync(_db, user, DateTime.UtcNow);
        var claims = UserTokens.Verify(issued.AuthToken, DateTime.UtcNow);
        Assert.NotNull(await UserTokens.AccountForAsync(_db, claims!, DateTime.UtcNow));

        await UserTokens.RevokeAsync(_db, issued.RefreshToken);

        Assert.NotNull(UserTokens.Verify(issued.AuthToken, DateTime.UtcNow));
        Assert.Null(await UserTokens.AccountForAsync(_db, claims!, DateTime.UtcNow));
    }

    [Fact]
    public async Task TokenWithoutSessionRefused()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var user = Jane();
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claims = UserTokens.Verify(UserTokens.Mint(user, "", DateTime.UtcNow), DateTime.UtcNow);
        Assert.Null(await UserTokens.AccountForAsync(_db, claims!, DateTime.UtcNow));
    }

    [Fact]
    public async Task OperatorSessionsShareStore()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var admin = await _db.UserAccounts.FirstAsync(u => u.Role == AccountRoles.Admin, TestContext.Current.CancellationToken);

        var issued = await UserTokens.IssueAsync(_db, admin, DateTime.UtcNow);
        Assert.NotNull(await UserTokens.ReauthAsync(_db, issued.RefreshToken, DateTime.UtcNow));

        await UserTokens.RevokeAllAsync(_db, admin.Id);
        Assert.Null(await UserTokens.ReauthAsync(_db, issued.RefreshToken, DateTime.UtcNow));
    }

    [Fact]
    public async Task SigningKeyIsAFile()
    {
        var dir = Directory.CreateTempSubdirectory("baseport-keystore").FullName;
        try
        {
            var dbPath = Path.Combine(dir, "baseport.db");
            var builder = new DbContextOptionsBuilder<AppDbContext>();
            AppDbContext.Configure(builder, $"Data Source={dbPath}");
            var options = builder.Options;

            string first;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
                await SchemaBootstrap.ApplyAsync(db);
                first = KeyStore.Read(db)!;
            }

            var keyFile = Path.Combine(dir, "baseport.key");
            Assert.True(File.Exists(keyFile));
            Assert.False(string.IsNullOrWhiteSpace(first));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));

            await using (var db = new AppDbContext(options))
            {
                await SchemaBootstrap.ApplyAsync(db);
                Assert.Equal(first, KeyStore.Read(db));
            }

            using var raw = new SqliteConnection($"Data Source={dbPath}");
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT * FROM _settings LIMIT 1";
            using var reader = cmd.ExecuteReader();
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            Assert.DoesNotContain("AuthSigningKey", columns);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MemoryDatabaseKeepsKeyInMemory()
    {
        Assert.Equal("", KeyStore.PathFor(_db));
        Assert.Null(KeyStore.Read(_db));
        KeyStore.Write(_db, "should-not-be-written");
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "baseport.key")));
    }

    [Fact]
    public async Task UsernameAndEmailConflictAlike()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        _db.UserAccounts.Add(Jane());
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(await UserAuthEndpoints.TakenAsync(_db, "jane", "other@example.com", ""));
        Assert.True(await UserAuthEndpoints.TakenAsync(_db, "other", "jane@example.com", ""));
        Assert.False(await UserAuthEndpoints.TakenAsync(_db, "other", "other@example.com", ""));
        Assert.False(await UserAuthEndpoints.TakenAsync(_db, "other", "", ""));
        Assert.False(await UserAuthEndpoints.TakenAsync(_db, "jane", "jane@example.com", "user00000001"));
    }

    [Theory]
    [InlineData("jane@example.com")]
    [InlineData("j@x.io")]
    [InlineData("a b c")]
    public void DerivedUsernameIsValid(string email) =>
        Assert.Empty(AccountValidation.Validate(UserAuthEndpoints.DeriveUsername(email), ""));

    [Fact]
    public void PublicAuthOffByDefault()
    {
        var settings = new AppSettings();
        Assert.False(settings.PublicAuthEnabled);
        Assert.False(settings.PublicRegistrationEnabled);
    }

    [Theory]
    [InlineData("avatars", true)]
    [InlineData("user-uploads", true)]
    [InlineData("Avatars", false)]
    [InlineData("../etc", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void BucketNameIsOneSegment(string bucket, bool valid) =>
        Assert.Equal(valid, FileStore.IsBucket(bucket));

    [Fact]
    public void SignUpLinkHiddenWhenClosed()
    {
        const string page = "<form><!--__SIGNUP__--><p><a href='/auth/register'>Create one</a></p><!--__/SIGNUP__--></form>";

        Assert.Equal("<form></form>", UserAuthEndpoints.Signup(page, false));

        var open = UserAuthEndpoints.Signup(page, true);
        Assert.Contains("/auth/register", open);
        Assert.DoesNotContain("__SIGNUP__", open);
    }

    [Fact]
    public void UploadNameIsUnguessable()
    {
        Assert.True(FileStore.NameLength * 6 >= 128);

        var minted = Enumerable.Range(0, 500).Select(_ => Ids.NewShortId(FileStore.NameLength)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(500, minted.Count);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("avatars/../../secret.db")]
    [InlineData("a/b/c.png")]
    public void StoredNameCannotEscape(string name)
    {
        FileStore.Initialize("Data Source=baseport.db");
        var resolved = FileStore.Resolve(name);
        Assert.True(resolved is null || Path.GetFullPath(resolved).StartsWith(Path.GetFullPath(FileStore.Directory), StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyUploadUrlsAreReferences()
    {
        var referenced = Jobs.ReferencedUploads(new[]
        {
            """{"avatar":"http://localhost/uploads/abc123.png","note":"see abc123.png in the folder"}""",
            """{"nested":{"docs":["http://localhost/uploads/deep456.pdf?v=2"]}}""",
            "not json at all"
        });

        Assert.Contains("abc123.png", referenced);
        Assert.Contains("deep456.pdf", referenced);
        Assert.DoesNotContain("see abc123.png in the folder", referenced);
        Assert.Equal(2, referenced.Count);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
