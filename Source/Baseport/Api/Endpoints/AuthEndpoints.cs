using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", LoginAsync).RequireRateLimiting(RateLimit.Auth);

        app.MapPost("/api/auth/otp", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            var username = (body["username"]?.GetValue<string>() ?? "").Trim();

            const string sent = "If that account exists, a code has been issued.";

            if (string.IsNullOrWhiteSpace(username))
                return Results.BadRequest(new { errors = new[] { "Enter a username first." } });

            var (code, retryAfter) = OneTimeCodes.Issue(username);
            if (code is null)
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                ctx.Response.Headers.RetryAfter = seconds.ToString();
                return Results.Json(new { errors = new[] { $"A code was just issued. Try again in {seconds} seconds." } },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Username == username);
            if (user is not null && !user.IsDisabled)
            {
                Serilog.Log.Warning("Sign-in code for {Username}: {Code} (expires in {Seconds} seconds)",
                    username, code, (int)OneTimeCodes.CodeLifetime.TotalSeconds);
            }
            else
            {

                Serilog.Log.Information("Sign-in code requested for an unknown or disabled account.");
            }

            return Results.Ok(new { message = sent, expiresInSeconds = (int)OneTimeCodes.CodeLifetime.TotalSeconds });
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapPost("/api/auth/logout", async (AppDbContext db, HttpContext ctx) =>
        {
            await UserTokens.RevokeAsync(db, ctx.Request.Cookies[AdminAuth.RefreshCookie] ?? "");
            AdminAuth.ClearCookies(ctx);
            return Results.Ok(new { signedOut = true });
        });

        app.MapGet("/api/auth/me", async (AppDbContext db, HttpContext ctx) =>
        {
            var user = await AdminAuth.ResolveAsync(db, ctx);
            if (user is null) return Results.Ok(new { authenticated = false });

            return Results.Ok(new { authenticated = true, user.Username, user.Email, user.Role, user.MustChangePassword, Totp = user.TotpEnabledAt is not null, Avatar = Avatars.DataUri(user.Username) });
        });

        app.MapPost("/api/auth/password", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            if (AdminAuth.NeedsHttps(ctx)) return Results.BadRequest(new { errors = new[] { AdminAuth.HttpsRequired } });
            var user = await AdminAuth.ResolveAsync(db, ctx);
            if (user is null) return Results.Unauthorized();

            var current = body["currentPassword"]?.GetValue<string>() ?? "";
            var next = body["newPassword"]?.GetValue<string>() ?? "";

            if (!LoginGuard.Allowed(LoginGuard.Key(user.Id, ctx)))
            {
                ctx.Response.Headers.RetryAfter = "300";
                return Results.Json(new { errors = new[] { "Too many attempts. Wait a few minutes and try again." } },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (AccountValidation.PasswordProblem(next) is { } problem)
                return Results.BadRequest(new { errors = new[] { problem } });

            if (next == current)
                return Results.BadRequest(new { errors = new[] { "The new password must be different from the current one." } });

            if (!AdminAuth.VerifyPassword(current, user.PasswordHash))
            {
                LoginGuard.Failed(LoginGuard.Key(user.Id, ctx));
                return Results.BadRequest(new { errors = new[] { "The current password is incorrect." } });
            }

            LoginGuard.Succeeded(LoginGuard.Key(user.Id, ctx));

            var now = DateTime.UtcNow;
            user.PasswordHash = AdminAuth.HashPassword(next);
            user.MustChangePassword = false;
            user.UpdatedAt = now;
            await db.SaveChangesAsync();

            await UserTokens.RevokeAllAsync(db, user.Id);
            AdminAuth.IssueCookies(ctx, await UserTokens.IssueAsync(db, user, now));
            return Results.Ok(new { changed = true });
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapPost("/api/auth/totp/setup", TotpSetupAsync).RequireRateLimiting(RateLimit.Auth);
        app.MapPost("/api/auth/totp/confirm", TotpConfirmAsync).RequireRateLimiting(RateLimit.Auth);
        app.MapDelete("/api/auth/totp", TotpDisableAsync).RequireRateLimiting(RateLimit.Auth);
    }

    internal static async Task<IResult> LoginAsync(AppDbContext db, HttpContext ctx, JsonObject body)
    {
        if (AdminAuth.NeedsHttps(ctx)) return Results.BadRequest(new { errors = new[] { AdminAuth.HttpsRequired } });
        var username = (body["username"]?.GetValue<string>() ?? "").Trim();
        var password = body["password"]?.GetValue<string>() ?? "";
        var otp = body["otp"]?.GetValue<string>() ?? "";

        var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Username == username);

        if (!LoginGuard.Allowed(LoginGuard.Key(username, ctx)))
        {
            ctx.Response.Headers.RetryAfter = "300";
            return Results.Json(new { errors = new[] { "Too many sign-in attempts. Wait a few minutes and try again." } },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var usable = user is not null && !user.IsDisabled;

        var ok = otp.Length > 0
            ? usable && OneTimeCodes.Consume(username, otp)
            : AdminAuth.CheckPassword(password, user);

        var credential = otp.Length > 0 ? "one-time code" : "password";

        if (!ok)
        {
            LoginGuard.Failed(LoginGuard.Key(username, ctx));

            AuditLogMiddleware.Note(ctx, $"Failed console sign-in as \"{username}\" with a {credential}");
            return Results.Json(new { errors = new[] { otp.Length > 0 ? "That code is not valid or has expired." : "Incorrect username or password." } },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var now = DateTime.UtcNow;
        if (otp.Length == 0)
        {
            var second = Totp.Check(user!, Text(body, "code"), now);
            if (second == SecondFactor.Required)
                return Results.Json(new { errors = new[] { Totp.CodeNeeded }, totp = true }, statusCode: StatusCodes.Status401Unauthorized);
            if (second == SecondFactor.Invalid)
            {
                LoginGuard.Failed(LoginGuard.Key(username, ctx));
                AuditLogMiddleware.Note(ctx, $"Failed console sign-in as \"{username}\" with an authenticator code");
                return Results.Json(new { errors = new[] { "That authenticator code is not valid." }, totp = true }, statusCode: StatusCodes.Status401Unauthorized);
            }
        }

        LoginGuard.Succeeded(LoginGuard.Key(username, ctx));
        user!.LastLoginAt = now;
        await db.SaveChangesAsync();

        AuditLogMiddleware.Note(ctx, $"Console sign-in as {user.Username} with a {credential}");
        AdminAuth.IssueCookies(ctx, await UserTokens.IssueAsync(db, user, now));
        return Results.Ok(new { user.Username, user.Role, user.MustChangePassword });
    }

    internal static async Task<IResult> TotpSetupAsync(AppDbContext db, HttpContext ctx)
    {
        var (user, refusal) = await TotpCallerAsync(db, ctx);
        if (user is null) return refusal!;
        if (user.TotpEnabledAt is not null) return Refuse("Two-factor sign-in is already on. Turn it off first.");

        var key = Totp.NewKey();
        user.TotpSecretProtected = Totp.Protect(key);
        user.TotpLastStep = 0;
        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        AuditLogMiddleware.Note(ctx, $"Two-factor setup started for {user.Username}");
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { secret = Totp.Base32(key), uri = Totp.Uri(UserTokens.Issuer, user.Username, key) });
    }

    internal static async Task<IResult> TotpConfirmAsync(AppDbContext db, HttpContext ctx, JsonObject body)
    {
        var (user, refusal) = await TotpCallerAsync(db, ctx);
        if (user is null) return refusal!;
        if (user.TotpEnabledAt is not null) return Refuse("Two-factor sign-in is already on.");
        if (user.TotpSecretProtected.Length == 0) return Refuse("Start the setup first.");

        var now = DateTime.UtcNow;
        if (!Totp.Verify(Totp.KeyOf(user), Text(body, "code").Trim(), 0, now, out var step))
            return Refuse("That code is not valid. Check the clock on your device.");

        user.TotpEnabledAt = now;
        user.TotpLastStep = step;
        user.UpdatedAt = now;
        await db.SaveChangesAsync();

        await UserTokens.RevokeAllAsync(db, user.Id);
        AdminAuth.IssueCookies(ctx, await UserTokens.IssueAsync(db, user, now));
        AuditLogMiddleware.Note(ctx, $"Two-factor sign-in turned on for {user.Username}");
        return Results.Ok(new { totp = true });
    }

    internal static async Task<IResult> TotpDisableAsync(AppDbContext db, HttpContext ctx, [Microsoft.AspNetCore.Mvc.FromBody] JsonObject body)
    {
        var (user, refusal) = await TotpCallerAsync(db, ctx);
        if (user is null) return refusal!;
        if (user.TotpEnabledAt is null) return Refuse("Two-factor sign-in is already off.");

        var key = LoginGuard.Key(user.Id, ctx);
        if (!LoginGuard.Allowed(key))
        {
            ctx.Response.Headers.RetryAfter = "300";
            return Results.Json(new { errors = new[] { "Too many attempts. Wait a few minutes and try again." } },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var now = DateTime.UtcNow;
        if (!AdminAuth.VerifyPassword(Text(body, "password"), user.PasswordHash)
            || Totp.Check(user, Text(body, "code"), now) != SecondFactor.Ok)
        {
            LoginGuard.Failed(key);
            return Refuse("The password or code is incorrect.");
        }

        LoginGuard.Succeeded(key);
        Totp.Clear(user);
        user.UpdatedAt = now;
        await db.SaveChangesAsync();

        AuditLogMiddleware.Note(ctx, $"Two-factor sign-in turned off for {user.Username}");
        return Results.Ok(new { totp = false });
    }

    private static async Task<(UserAccount? User, IResult? Refusal)> TotpCallerAsync(AppDbContext db, HttpContext ctx)
    {
        if (AdminAuth.NeedsHttps(ctx)) return (null, Refuse(AdminAuth.HttpsRequired));
        var user = await AdminAuth.ResolveAsync(db, ctx);
        if (user is null) return (null, Results.Unauthorized());
        if (user.Role != AccountRoles.Admin || user.MustChangePassword)
            return (null, Results.Json(new { errors = new[] { "Two-factor sign-in is for admin accounts with their own password set." } },
                statusCode: StatusCodes.Status403Forbidden));
        return (user, null);
    }

    private static IResult Refuse(string message) => Results.BadRequest(new { errors = new[] { message } });

    private static string Text(JsonObject body, string name) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
