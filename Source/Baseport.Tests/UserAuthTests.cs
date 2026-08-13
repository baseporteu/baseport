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
    public void A_minted_token_verifies_and_carries_its_claims()
    {
        var now = DateTime.UtcNow;
        var claims = UserTokens.Verify(UserTokens.Mint(Jane(), now), now);

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
        var parts = UserTokens.Mint(Jane(), now).Split('.');
        var forged = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"iss":"baseport","aud":"baseport","sub":"admin","iat":0,"exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Null(UserTokens.Verify($"{parts[0]}.{forged}.{parts[2]}", now));
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        var issued = DateTime.UtcNow.AddDays(-1);
        Assert.Null(UserTokens.Verify(UserTokens.Mint(Jane(), issued), DateTime.UtcNow));
    }

    [Fact]
    public void Changing_the_issuer_rejects_tokens_minted_under_the_old_one()
    {
        var now = DateTime.UtcNow;
        var token = UserTokens.Mint(Jane(), now);

        UserTokens.Configure(new AppSettings { AuthIssuer = "acme" });
        Assert.Null(UserTokens.Verify(token, now));

        UserTokens.Configure(new AppSettings());
        Assert.NotNull(UserTokens.Verify(token, now));
    }

    [Fact]
    public void Rotating_the_signing_key_rejects_tokens_minted_under_the_old_one()
    {
        var now = DateTime.UtcNow;
        var token = UserTokens.Mint(Jane(), now);

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

    [Fact]
    public async Task A_refresh_token_is_spent_by_the_refresh_it_pays_for()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var user = Jane();
        _db.UserAccounts.Add(user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var issued = await UserTokens.IssueAsync(_db, user, DateTime.UtcNow);
        var rotated = await UserTokens.RefreshAsync(_db, issued.RefreshToken, DateTime.UtcNow);

        Assert.NotNull(rotated);
        Assert.NotEqual(issued.RefreshToken, rotated!.RefreshToken);
        Assert.Null(await UserTokens.RefreshAsync(_db, issued.RefreshToken, DateTime.UtcNow));
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

        Assert.Null(await UserTokens.RefreshAsync(_db, issued.RefreshToken, DateTime.UtcNow));
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

        Assert.Null(await UserTokens.RefreshAsync(_db, first.RefreshToken, DateTime.UtcNow));
        Assert.Null(await UserTokens.RefreshAsync(_db, second.RefreshToken, DateTime.UtcNow));
    }

    [Fact]
    public async Task An_admin_account_is_not_a_public_auth_account()
    {
        await SchemaBootstrap.ApplyAsync(_db);
        var admin = await _db.UserAccounts.FirstAsync(u => u.Role == AccountRoles.Admin, TestContext.Current.CancellationToken);

        var issued = await UserTokens.IssueAsync(_db, admin, DateTime.UtcNow);
        Assert.Null(await UserTokens.RefreshAsync(_db, issued.RefreshToken, DateTime.UtcNow));
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
