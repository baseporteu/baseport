using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Baseport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Baseport.Tests;

public class TotpVectorTests
{
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void The_rfc_6238_sha1_vectors_hold(long unixSeconds, string expected) =>
        Assert.Equal(expected, Totp.Code(RfcKey, unixSeconds / 30));

    [Fact]
    public void A_code_from_the_previous_or_next_step_is_accepted()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111).UtcDateTime;
        var step = 1111111111L / 30;

        Assert.True(Totp.Verify(RfcKey, Totp.Code(RfcKey, step - 1), 0, now, out _));
        Assert.True(Totp.Verify(RfcKey, Totp.Code(RfcKey, step + 1), 0, now, out _));
        Assert.False(Totp.Verify(RfcKey, Totp.Code(RfcKey, step - 2), 0, now, out _));
        Assert.False(Totp.Verify(RfcKey, Totp.Code(RfcKey, step + 2), 0, now, out _));
    }

    [Fact]
    public void A_step_already_used_is_refused()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111).UtcDateTime;
        var code = Totp.Code(RfcKey, 1111111111L / 30);

        Assert.True(Totp.Verify(RfcKey, code, 0, now, out var used));
        Assert.False(Totp.Verify(RfcKey, code, used, now, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("0508471")]
    public void A_malformed_code_is_refused(string code) =>
        Assert.False(Totp.Verify(RfcKey, code, 0, DateTime.UtcNow, out _));

    [Fact]
    public void The_key_is_shown_as_base32() =>
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(RfcKey));

    [Fact]
    public void The_uri_names_issuer_and_account()
    {
        var uri = Totp.Uri("baseport", "jane", RfcKey);
        Assert.Equal("otpauth://totp/baseport:jane?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=baseport", uri);
    }
}

[Collection(nameof(UserAuthTests))]
public class TotpDoorTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public TotpDoorTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();
        _db = TestDb.Open(_connection);
        _db.Database.EnsureCreated();
        _db.AppSettings.Add(new AppSettings { PublicAuthEnabled = true });
        _db.SaveChanges();
        Secrets.Configure(new EphemeralDataProtectionProvider());
        UserTokens.Initialize(null);
        UserTokens.Configure(new AppSettings());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<(UserAccount Account, byte[] Key)> EnrolledAdminAsync(string username)
    {
        var key = Totp.NewKey();
        var account = new UserAccount
        {
            Id = Ids.NewShortId(12),
            Username = username,
            Role = AccountRoles.Admin,
            PasswordHash = AdminAuth.HashPassword(Password),
            TotpSecretProtected = Secrets.Protect(Convert.ToBase64String(key)),
            TotpEnabledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (account, key);
    }

    private static string Now(byte[] key) => Totp.Code(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

    private static HttpContext Request(string? cookie = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("localhost");
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        if (cookie is not null) ctx.Request.Headers.Cookie = $"{AdminAuth.AuthCookie}={cookie}";
        return ctx;
    }

    private static int Status(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static async Task<string> BodyAsync(IResult result)
    {
        var ctx = Request();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await new StreamReader(ctx.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private Task<IResult> ConsoleAsync(string username, string? code) =>
        AuthEndpoints.LoginAsync(_db, Request(), new JsonObject { ["username"] = username, ["password"] = Password, ["code"] = code });

    private Task<IResult> PublicAsync(string username, string? code) =>
        UserAuthEndpoints.LoginAsync(_db, Request(), new JsonObject { ["email_or_username"] = username, ["password"] = Password, ["totp_code"] = code });

    [Fact]
    public async Task An_enrolled_admin_cannot_sign_in_at_either_door_without_a_code()
    {
        var (_, key) = await EnrolledAdminAsync("totp-doors");

        var console = await ConsoleAsync("totp-doors", null);
        Assert.Equal(401, Status(console));
        Assert.Contains("\"totp\":true", await BodyAsync(console));

        var open = await PublicAsync("totp-doors", null);
        Assert.Equal(401, Status(open));
        Assert.Contains("\"totp\":true", await BodyAsync(open));

        Assert.Equal(200, Status(await ConsoleAsync("totp-doors", Now(key))));
    }

    [Fact]
    public async Task The_public_door_accepts_a_valid_code()
    {
        var (_, key) = await EnrolledAdminAsync("totp-public");
        Assert.Equal(200, Status(await PublicAsync("totp-public", Now(key))));
    }

    [Fact]
    public async Task A_wrong_password_never_reveals_the_code_prompt()
    {
        await EnrolledAdminAsync("totp-wrongpw");
        var result = await AuthEndpoints.LoginAsync(_db, Request(),
            new JsonObject { ["username"] = "totp-wrongpw", ["password"] = "not it at all" });

        Assert.Equal(401, Status(result));
        Assert.DoesNotContain("totp", await BodyAsync(result));
    }

    [Fact]
    public async Task A_wrong_code_counts_toward_the_lockout()
    {
        var (_, key) = await EnrolledAdminAsync("totp-lockout");

        for (var i = 0; i < 5; i++)
            Assert.Equal(401, Status(await ConsoleAsync("totp-lockout", "000000" == Now(key) ? "111111" : "000000")));

        Assert.Equal(429, Status(await ConsoleAsync("totp-lockout", Now(key))));
    }

    [Fact]
    public async Task A_code_cannot_be_used_twice()
    {
        var (_, key) = await EnrolledAdminAsync("totp-replay");
        var code = Now(key);

        Assert.Equal(200, Status(await ConsoleAsync("totp-replay", code)));
        Assert.Equal(401, Status(await ConsoleAsync("totp-replay", code)));
    }

    [Fact]
    public async Task A_one_time_code_sign_in_is_not_asked_for_totp()
    {
        await EnrolledAdminAsync("totp-otp");
        var (otp, _) = OneTimeCodes.Issue("totp-otp");

        var result = await AuthEndpoints.LoginAsync(_db, Request(),
            new JsonObject { ["username"] = "totp-otp", ["otp"] = otp });

        Assert.Equal(200, Status(result));
    }

    private async Task<(UserAccount Account, string Cookie)> SignedInAsync(string username, string role)
    {
        var account = new UserAccount
        {
            Id = Ids.NewShortId(12),
            Username = username,
            Role = role,
            PasswordHash = AdminAuth.HashPassword(Password),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.UserAccounts.Add(account);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var tokens = await UserTokens.IssueAsync(_db, account, DateTime.UtcNow);
        return (account, tokens.AuthToken);
    }

    [Fact]
    public async Task Setup_is_refused_for_a_non_admin()
    {
        var (_, cookie) = await SignedInAsync("totp-consumer", AccountRoles.Consumer);
        Assert.Equal(403, Status(await AuthEndpoints.TotpSetupAsync(_db, Request(cookie))));
    }

    [Fact]
    public async Task Setup_is_self_only()
    {
        var (admin, cookie) = await SignedInAsync("totp-self", AccountRoles.Admin);
        var (other, _) = await SignedInAsync("totp-other", AccountRoles.Admin);

        Assert.Equal(200, Status(await AuthEndpoints.TotpSetupAsync(_db, Request(cookie))));

        _db.ChangeTracker.Clear();
        Assert.NotEqual("", (await _db.UserAccounts.FirstAsync(a => a.Id == admin.Id, TestContext.Current.CancellationToken)).TotpSecretProtected);
        Assert.Equal("", (await _db.UserAccounts.FirstAsync(a => a.Id == other.Id, TestContext.Current.CancellationToken)).TotpSecretProtected);
    }

    [Fact]
    public async Task Confirm_enables_totp_and_revokes_other_sessions()
    {
        var (admin, cookie) = await SignedInAsync("totp-confirm", AccountRoles.Admin);
        await UserTokens.IssueAsync(_db, admin, DateTime.UtcNow);

        var setup = JsonNode.Parse(await BodyAsync(await AuthEndpoints.TotpSetupAsync(_db, Request(cookie))))!;
        _db.ChangeTracker.Clear();
        var stored = await _db.UserAccounts.FirstAsync(a => a.Id == admin.Id, TestContext.Current.CancellationToken);
        var key = Convert.FromBase64String(Secrets.Unprotect(stored.TotpSecretProtected));
        Assert.Equal(Totp.Base32(key), setup["secret"]!.GetValue<string>());

        Assert.Equal(400, Status(await AuthEndpoints.TotpConfirmAsync(_db, Request(cookie), new JsonObject { ["code"] = "12345" })));

        var ctx = Request(cookie);
        Assert.Equal(200, Status(await AuthEndpoints.TotpConfirmAsync(_db, ctx, new JsonObject { ["code"] = Now(key) })));

        _db.ChangeTracker.Clear();
        Assert.NotNull((await _db.UserAccounts.FirstAsync(a => a.Id == admin.Id, TestContext.Current.CancellationToken)).TotpEnabledAt);
        Assert.Equal(1, await _db.UserSessions.CountAsync(s => s.UserId == admin.Id, TestContext.Current.CancellationToken));
        Assert.Contains(AdminAuth.AuthCookie, ctx.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public async Task Setup_is_refused_once_enabled()
    {
        var (admin, cookie) = await SignedInAsync("totp-twice", AccountRoles.Admin);
        admin.TotpSecretProtected = Secrets.Protect(Convert.ToBase64String(Totp.NewKey()));
        admin.TotpEnabledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(400, Status(await AuthEndpoints.TotpSetupAsync(_db, Request(cookie))));
    }

    [Fact]
    public async Task Turning_off_needs_both_password_and_code()
    {
        var (admin, cookie) = await SignedInAsync("totp-off", AccountRoles.Admin);
        var key = Totp.NewKey();
        admin.TotpSecretProtected = Secrets.Protect(Convert.ToBase64String(key));
        admin.TotpEnabledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(400, Status(await AuthEndpoints.TotpDisableAsync(_db, Request(cookie), new JsonObject { ["password"] = Password })));
        Assert.Equal(400, Status(await AuthEndpoints.TotpDisableAsync(_db, Request(cookie), new JsonObject { ["password"] = "wrong password here", ["code"] = Now(key) })));
        Assert.Equal(200, Status(await AuthEndpoints.TotpDisableAsync(_db, Request(cookie), new JsonObject { ["password"] = Password, ["code"] = Now(key) })));

        _db.ChangeTracker.Clear();
        var stored = await _db.UserAccounts.FirstAsync(a => a.Id == admin.Id, TestContext.Current.CancellationToken);
        Assert.Null(stored.TotpEnabledAt);
        Assert.Equal("", stored.TotpSecretProtected);
    }
}
