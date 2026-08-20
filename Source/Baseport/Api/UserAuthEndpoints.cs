using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class UserAuthEndpoints
{
    private const string ApiBase = "/api/auth/v1";
    private const string UiBase = "/auth";

    private static readonly string DummyHash = AdminAuth.HashPassword("constant-time-decoy");

    public static void MapUserAuthEndpoints(this WebApplication app)
    {
        var webRoot = app.Environment.WebRootPath;

        app.MapGet($"{ApiBase}/jwks.json", async (AppDbContext db) =>
            await EnabledAsync(db) ? Results.Json(UserTokens.Jwks()) : Results.NotFound());

        // A visitor carries data before deciding to sign up. No credential is set, so this account cannot sign in again: the token pair it goes home with is the only way back to it, and register claims it when the visitor commits.
        app.MapPost($"{ApiBase}/anonymous", async (AppDbContext db, HttpContext ctx) =>
        {
            var settings = await db.SettingsAsync() ?? new AppSettings();
            if (!settings.PublicAuthEnabled) return Results.NotFound();
            if (!settings.AnonymousAuthEnabled)
                return Error(403, "Anonymous accounts are not enabled on this instance.");

            var now = DateTime.UtcNow;
            var user = new UserAccount
            {
                Id = Ids.NewShortId(12),
                Username = "anon-" + Ids.NewShortId(10),
                Role = AccountRoles.User,
                IsAnonymous = true,
                CreatedAt = now,
                UpdatedAt = now,
                LastLoginAt = now
            };
            db.UserAccounts.Add(user);
            await db.SaveChangesAsync();

            AuditLogMiddleware.Note(ctx, $"Anonymous account {user.Username} created");
            return Results.Created($"{ApiBase}/status", TokenPayload(await UserTokens.IssueAsync(db, user, now)));
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapPost($"{ApiBase}/register", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            var settings = await db.SettingsAsync() ?? new AppSettings();
            if (!settings.PublicAuthEnabled) return Results.NotFound();
            if (!settings.PublicRegistrationEnabled)
                return Error(403, "Sign-up is closed on this instance.");

            var email = Text(body, "email").Trim();
            var password = Text(body, "password");
            var username = Text(body, "username").Trim();

            if (username.Length == 0) username = DeriveUsername(email);

            var errors = AccountValidation.Validate(username, email);
            if (AccountValidation.PasswordProblem(password) is { } problem) errors.Add(problem);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            // Signing up while carrying an anonymous token claims that account rather than opening a second one, so the rows the visitor already created stay theirs. Any other caller registers as before.
            var claiming = await CurrentAsync(db, ctx) is { IsAnonymous: true } anonymous ? anonymous : null;

            if (await db.UserAccounts.AnyAsync(u => u.Username == username && u.Id != (claiming == null ? "" : claiming.Id)))
                return Error(409, "That username is taken.");
            if (email.Length > 0 && await db.UserAccounts.AnyAsync(u => u.Email == email && u.Role == AccountRoles.User && u.Id != (claiming == null ? "" : claiming.Id)))
                return Error(409, "That email is already registered.");

            var now = DateTime.UtcNow;
            var user = claiming ?? new UserAccount
            {
                Id = Ids.NewShortId(12),
                Role = AccountRoles.User,
                CreatedAt = now
            };
            user.Username = username;
            user.Email = email;
            user.PasswordHash = AdminAuth.HashPassword(password);
            user.IsAnonymous = false;
            user.UpdatedAt = now;

            if (claiming is null) db.UserAccounts.Add(user);
            await db.SaveChangesAsync();

            // The anonymous token was a bearer credential for an account that now has a password, so it does not outlive the claim.
            if (claiming is not null)
            {
                await UserTokens.RevokeAllAsync(db, user.Id);
                AuditLogMiddleware.Note(ctx, $"Anonymous account claimed as {user.Username}");
            }

            var tokens = await UserTokens.IssueAsync(db, user, now);
            return Results.Created($"{ApiBase}/status", TokenPayload(tokens));
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapPost($"{ApiBase}/login", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            var handle = Text(body, "email_or_username").Trim();
            var password = Text(body, "password");

            if (!LoginGuard.Allowed($"user:{handle}"))
            {
                ctx.Response.Headers.RetryAfter = "300";
                return Error(429, "Too many sign-in attempts. Wait a few minutes and try again.");
            }

            // Every role signs in here, the way TrailBase does it: what a caller may then do is decided per request by re-reading the account, never by which door they came through.
            var user = await db.UserAccounts.FirstOrDefaultAsync(u =>
                u.Username == handle || (u.Email == handle && handle != ""));

            var usable = user is not null && !user.IsDisabled && user.PasswordHash.Length > 0;
            var ok = usable
                ? AdminAuth.VerifyPassword(password, user!.PasswordHash)
                : AdminAuth.VerifyPassword(password, DummyHash);

            if (!ok)
            {
                LoginGuard.Failed($"user:{handle}");
                AuditLogMiddleware.Note(ctx, $"Failed end-user sign-in as \"{handle}\" with a password");
                return Error(401, "Incorrect credentials.");
            }

            LoginGuard.Succeeded($"user:{handle}");
            var now = DateTime.UtcNow;
            user!.LastLoginAt = now;
            await db.SaveChangesAsync();

            AuditLogMiddleware.Note(ctx, $"End-user sign-in as {user.Username} with a password");
            return Results.Ok(TokenPayload(await UserTokens.IssueAsync(db, user, now)));
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapPost($"{ApiBase}/refresh", async (AppDbContext db, JsonObject body) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            var reauth = await UserTokens.ReauthAsync(db, Text(body, "refresh_token"), DateTime.UtcNow);
            return reauth is null
                ? Error(401, "That refresh token is not valid or has expired.")
                : Results.Ok(TokenPayload(reauth.Value.Tokens));
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapPost($"{ApiBase}/logout", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            await UserTokens.RevokeAsync(db, Text(body, "refresh_token"));
            // A cookie session signed in here too, so signing out has to reach it.
            await UserTokens.RevokeAsync(db, ctx.Request.Cookies[AdminAuth.RefreshCookie] ?? "");
            AdminAuth.ClearCookies(ctx);
            return Results.Ok(new { signed_out = true });
        });

        app.MapGet($"{ApiBase}/status", async (AppDbContext db, HttpContext ctx) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            var user = await CurrentAsync(db, ctx);
            return user is null
                ? Results.Ok(new { authenticated = false })
                : Results.Ok(new
                {
                    authenticated = true,
                    sub = user.Id,
                    user.Username,
                    user.Email,
                    user.Role,
                    anonymous = user.IsAnonymous
                });
        });

        app.MapPost($"{ApiBase}/change_password", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            var user = await CurrentAsync(db, ctx);
            if (user is null) return Error(401, "Sign in to continue.");

            var current = Text(body, "current_password");
            var next = Text(body, "new_password");

            if (!LoginGuard.Allowed($"user:{user.Id}"))
            {
                ctx.Response.Headers.RetryAfter = "300";
                return Error(429, "Too many attempts. Wait a few minutes and try again.");
            }
            if (AccountValidation.PasswordProblem(next) is { } problem)
                return Results.BadRequest(new { errors = new[] { problem } });
            if (next == current)
                return Results.BadRequest(new { errors = new[] { "The new password must be different from the current one." } });
            if (!AdminAuth.VerifyPassword(current, user.PasswordHash))
            {
                LoginGuard.Failed($"user:{user.Id}");
                return Results.BadRequest(new { errors = new[] { "The current password is incorrect." } });
            }

            LoginGuard.Succeeded($"user:{user.Id}");
            user.PasswordHash = AdminAuth.HashPassword(next);
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await UserTokens.RevokeAllAsync(db, user.Id);

            return Results.Ok(TokenPayload(await UserTokens.IssueAsync(db, user, DateTime.UtcNow)));
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapDelete($"{ApiBase}/delete", async (AppDbContext db, HttpContext ctx) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            var user = await CurrentAsync(db, ctx);
            if (user is null) return Error(401, "Sign in to continue.");

            // Every role signs in here now, so this route reaches accounts the console refuses to delete. The same two guards apply, or an operator locks everybody out from the public surface.
            if (await db.UserAccounts.CountAsync(a => a.Id != user.Id && !a.IsDisabled) == 0)
                return Error(409, "This is the last enabled account and cannot be deleted.");
            if (await AdminEndpoints.IsLastEnabledAdmin(db, user))
                return Error(409, "This is the last enabled admin and cannot be deleted.");

            await UserTokens.RevokeAllAsync(db, user.Id);
            db.UserAccounts.Remove(user);
            AdminAuth.ClearCookies(ctx);
            await db.SaveChangesAsync();
            return Results.Ok(new { deleted = true });
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapGet(UiBase, Entry);
        app.MapGet($"{UiBase}/{{**rest}}", Entry);

        foreach (var page in new[] { "login", "register", "profile" })
        {
            var name = page;
            app.MapGet($"{UiBase}/{name}", async (AppDbContext db, HttpContext ctx) =>
            {
                var settings = await db.SettingsAsync() ?? new AppSettings();
                if (!settings.PublicAuthEnabled) return Results.NotFound();
                if (name == "register" && !settings.PublicRegistrationEnabled) return Results.NotFound();

                ctx.Response.Headers.CacheControl = "no-store";
                var html = await File.ReadAllTextAsync(Path.Combine(webRoot, "auth", $"{name}.html"), ctx.RequestAborted);
                html = Signup(html, settings.PublicRegistrationEnabled);
                // Same reason the console renders its own: the sign-in screen paints its provider buttons on the first byte instead of after a round trip.
                if (html.Contains(BootstrapMarker, StringComparison.Ordinal))
                    html = html.Replace(BootstrapMarker,
                        Html.BootstrapScript(new { providers = await OidcEndpoints.OfferedAsync(db, console: false) }),
                        StringComparison.Ordinal);
                return Results.Content(html, "text/html; charset=utf-8");
            });
        }
    }

    private const string BootstrapMarker = "<!--__BOOTSTRAP__-->";

    internal static string Signup(string html, bool enabled)
    {
        const string open = "<!--__SIGNUP__-->";
        const string close = "<!--__/SIGNUP__-->";

        var start = html.IndexOf(open, StringComparison.Ordinal);
        var end = html.IndexOf(close, StringComparison.Ordinal);
        if (start < 0 || end < start) return html;

        return enabled
            ? html.Remove(end, close.Length).Remove(start, open.Length)
            : html.Remove(start, end + close.Length - start);
    }

    private static async Task<IResult> Entry(AppDbContext db) =>
        await EnabledAsync(db) ? Results.Redirect($"{UiBase}/login") : Results.NotFound();

    public static async Task<UserAccount?> CurrentAsync(AppDbContext db, HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTime.UtcNow;
            var claims = UserTokens.Verify(header["Bearer ".Length..].Trim(), now);
            return claims is null ? null : await UserTokens.AccountForAsync(db, claims, now);
        }

        // Headers take priority, cookies are the fallback: one sign-in on this origin is a sign-in on both surfaces. Same precedence as TrailBase's extract_tokens_from_request_parts, and the same reason a cookie can be reminted here where a header cannot.
        return await AdminAuth.ResolveAsync(db, ctx);
    }

    internal static string DeriveUsername(string email)
    {
        var local = email.Contains('@') ? email[..email.IndexOf('@')] : email;
        var cleaned = new string(local.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        return cleaned.Length >= AccountValidation.UsernameMin
            ? cleaned[..Math.Min(cleaned.Length, AccountValidation.UsernameMax - 7)] + "-" + Ids.NewShortId(6)
            : "user-" + Ids.NewShortId(8);
    }

    private static async Task<bool> EnabledAsync(AppDbContext db) =>
        (await db.SettingsAsync() ?? new AppSettings()).PublicAuthEnabled;

    private static object TokenPayload(UserTokenPair tokens) => new
    {
        auth_token = tokens.AuthToken,
        refresh_token = tokens.RefreshToken,
        expires_at = new DateTimeOffset(tokens.ExpiresAt, TimeSpan.Zero).ToUnixTimeSeconds()
    };

    private static string Text(JsonObject body, string name) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static IResult Error(int status, string message) =>
        Results.Json(new { errors = new[] { message } }, statusCode: status);
}
