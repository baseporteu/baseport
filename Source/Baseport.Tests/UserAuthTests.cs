using Xunit;
using Baseport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

public class UserAuthTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public UserAuthTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
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
    public void A_minted_token_verifies_and_transports_its_claims()
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
    public void A_tampered_payload_is_refused()
    {
        var now = DateTime.UtcNow;
        var parts = UserTokens.Mint(Jane(), "session00001", now).Split('.');
        var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"iss":"baseport","aud":"baseport","sub":"admin","iat":0,"exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(UserTokens.Verify($"{parts[0]}.{forged}.{parts[2]}", now));
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        var issued = DateTime.UtcNow.AddDays(-1);
        Assert.Null(UserTokens.Verify(UserTokens.Mint(Jane(), "session00001", issued), DateTime.UtcNow));
    }

    [Fact]
    public void Changing_the_issuer_rejects_tokens_minted_under_the_old_one()
    {
        var now = DateTime.UtcNow;
        var token = UserTokens.Mint(Jane(), "session00001", now);

        UserTokens.Configure(new AppSettings { AuthIssuer = "acme" });
        Assert.Null(UserTokens.Verify(token, now));

        UserTokens.Configure(new AppSettings());
        Assert.NotNull(UserTokens.Verify(token, now));
    }

    [Fact]
    public void Rotating_the_signing_key_rejects_tokens_minted_under_the_old_one()
    {
        var now = DateTime.UtcNow;
        var token = UserTokens.Mint(Jane(), "session00001", now);

        UserTokens.Rotate();
        Assert.Null(UserTokens.Verify(token, now));
    }

    [Fact]
    public void A_configured_lifetime_is_clamped_to_the_allowed_range()
    {
        UserTokens.Configure(new AppSettings { AuthTokenLifetimeSec = 5, AuthRefreshLifetimeDays = 5000 });
        Assert.Equal(UserTokens.MinTokenLifetimeSec, (int)UserTokens.AuthTokenLifetime.TotalSeconds);
        Assert.Equal(UserTokens.MaxRefreshLifetimeDays, (int)UserTokens.RefreshTokenLifetime.TotalDays);
        UserTokens.Configure(new AppSettings());
    }

    // Not rotated, deliberately, two clients refreshing the same session at once would otherwise leave one of them holding a token that has already been spent.
    [Fact]
    public async Task A_refresh_token_survives_the_refresh_it_pays_for()
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
    public async Task An_expired_session_row_cannot_reauth_and_is_pruned()
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
    public async Task A_disabled_account_cannot_refresh()
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
    public async Task Revoking_a_users_sessions_kills_every_refresh_token()
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

    // A stateless access token cannot be recalled, it names its session row and every resolution re-reads it: without that, signing out revoked the refresh token and left the access token good for its full lifetime.
    [Fact]
    public async Task Revoking_a_session_kills_the_access_token_it_issued()
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

    // A token minted before the session claim existed names no session, and is refused instead of trusted.
    [Fact]
    public async Task An_access_token_without_a_session_claim_is_refused()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var user = Jane();
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claims = UserTokens.Verify(UserTokens.Mint(user, "", DateTime.UtcNow), DateTime.UtcNow);
        Assert.Null(await UserTokens.AccountForAsync(_db, claims!, DateTime.UtcNow));
    }

    // One session store for every role: revoking an operator's sessions has to reach the console, which it only can if the console session is a row in the same table.
    [Fact]
    public async Task An_operators_session_lives_in_the_same_store_as_an_end_users()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var admin = await _db.UserAccounts.FirstAsync(u => u.Role == AccountRoles.Admin, TestContext.Current.CancellationToken);

        var issued = await UserTokens.IssueAsync(_db, admin, DateTime.UtcNow);
        Assert.NotNull(await UserTokens.ReauthAsync(_db, issued.RefreshToken, DateTime.UtcNow));

        await UserTokens.RevokeAllAsync(_db, admin.Id);
        Assert.Null(await UserTokens.ReauthAsync(_db, issued.RefreshToken, DateTime.UtcNow));
    }

    // The signing key forges an admin cookie, it must not be a row anything with SQL can select. It lives beside the database instead, owner-readable only, and survives a restart so a running instance's tokens do not all die on a reboot.
    [Fact]
    public async Task The_signing_key_lives_in_a_file_beside_the_database_and_not_in_a_row()
    {
        var dir = Directory.CreateTempSubdirectory("baseport-keystore").FullName;
        try
        {
            var dbPath = Path.Combine(dir, "baseport.db");
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;

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

    // An in-memory database dies with the process, writing its key to the working directory would leave a real credential lying around after a test run.
    [Fact]
    public void An_in_memory_database_keeps_its_key_in_memory()
    {
        Assert.Equal("", KeyStore.PathFor(_db));
        Assert.Null(KeyStore.Read(_db));
        KeyStore.Write(_db, "should-not-be-written");
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "baseport.key")));
    }

    [Theory]
    [InlineData("jane@example.com")]
    [InlineData("j@x.io")]
    [InlineData("a b c")]
    public void A_derived_username_is_always_valid(string email) =>
        Assert.Empty(AccountValidation.Validate(UserAuthEndpoints.DeriveUsername(email), ""));

    [Fact]
    public void Public_auth_is_off_until_an_operator_turns_it_on()
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
    public void A_bucket_name_is_a_single_safe_path_segment(string bucket, bool valid) =>
        Assert.Equal(valid, FileStore.IsBucket(bucket));

    [Fact]
    public void The_sign_up_link_is_gone_from_the_page_when_sign_up_is_closed()
    {
        const string page = "<form><!--__SIGNUP__--><p><a href='/auth/register'>Create one</a></p><!--__/SIGNUP__--></form>";

        Assert.Equal("<form></form>", UserAuthEndpoints.Signup(page, false));

        var open = UserAuthEndpoints.Signup(page, true);
        Assert.Contains("/auth/register", open);
        Assert.DoesNotContain("__SIGNUP__", open);
    }

    [Fact]
    public void An_upload_name_is_a_capability_key_in_its_own_right()
    {
        Assert.True(FileStore.NameLength * 6 >= 128);

        var minted = Enumerable.Range(0, 500).Select(_ => Ids.NewShortId(FileStore.NameLength)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(500, minted.Count);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("avatars/../../secret.db")]
    [InlineData("a/b/c.png")]
    public void A_stored_name_can_never_escape_the_uploads_directory(string name)
    {
        FileStore.Initialize("Data Source=baseport.db");
        var resolved = FileStore.Resolve(name);
        Assert.True(resolved is null || Path.GetFullPath(resolved).StartsWith(Path.GetFullPath(FileStore.Directory), StringComparison.Ordinal));
    }

    [Fact]
    public void Only_real_upload_urls_count_as_references()
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
