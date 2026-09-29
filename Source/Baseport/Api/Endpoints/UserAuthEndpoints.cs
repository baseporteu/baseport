using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class UserAuthEndpoints
{
    private const string ApiBase = "/api/auth/v1";
    private const string UiBase = "/auth";

    public static void MapUserAuthEndpoints(this WebApplication app)
    {
        var webRoot = app.Environment.WebRootPath;

        app.MapGet($"{ApiBase}/jwks.json", async (AppDbContext db) =>
            await EnabledAsync(db) ? Results.Json(UserTokens.Jwks()) : Results.NotFound()).RequireRateLimiting(RateLimit.Docs);

        app.MapPost($"{ApiBase}/anonymous", async (AppDbContext db, HttpContext ctx) =>
        {
            var settings = await db.SettingsAsync() ?? new AppSettings();
            if (!Enabled(settings)) return Results.NotFound();
            if (!AnonymousOpen(settings))
                return Error(ctx, ApiProblem.Forbidden, "Anonymous accounts are not enabled on this instance.");

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
            return Results.Created($"{ApiBase}/status", TokenPairDto.From(await UserTokens.IssueAsync(db, user, now)));
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapPost($"{ApiBase}/register", async (AppDbContext db, HttpContext ctx, RegisterRequest body) =>
        {
            var settings = await db.SettingsAsync() ?? new AppSettings();
            if (!Enabled(settings)) return Results.NotFound();
            if (!RegistrationOpen(settings))
                return Error(ctx, ApiProblem.Forbidden, "Sign-up is closed on this instance.");

            var email = (body.Email ?? "").Trim();
            var password = body.Password ?? "";
            var username = (body.Username ?? "").Trim();

            if (username.Length == 0) username = DeriveUsername(email);

            var errors = AccountValidation.Validate(username, email);
            if (AccountValidation.PasswordProblem(password) is { } problem) errors.Add(problem);
            if (errors.Count > 0) return ApiProblems.Write(ctx, ApiProblem.Unprocessable, errors);

            var claiming = await CurrentAsync(db, ctx) is { IsAnonymous: true } anonymous ? anonymous : null;

            // hash before lookup to equalize timing
            var hash = AdminAuth.HashPassword(password);

            if (await TakenAsync(db, username, email, claiming?.Id ?? ""))
            {
                Serilog.Log.Warning("Registration refused for {Client}: username or email in use", RateLimit.ClientKey(ctx));
                return Error(ctx, ApiProblem.Conflict, "That username or email is already registered.");
            }

            var now = DateTime.UtcNow;
            var user = claiming ?? new UserAccount
            {
                Id = Ids.NewShortId(12),
                Role = AccountRoles.User,
                CreatedAt = now
            };
            user.Username = username;
            user.Email = email;
            user.PasswordHash = hash;
            user.IsAnonymous = false;
            user.UpdatedAt = now;

            if (claiming is null) db.UserAccounts.Add(user);
            await db.SaveChangesAsync();

            if (claiming is not null)
            {
                await UserTokens.RevokeAllAsync(db, user.Id);
                AuditLogMiddleware.Note(ctx, $"Anonymous account claimed as {user.Username}");
            }

            var tokens = await UserTokens.IssueAsync(db, user, now);
            return Results.Created($"{ApiBase}/status", TokenPairDto.From(tokens));
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapPost($"{ApiBase}/login", LoginAsync).RequireRateLimiting(RateLimit.Auth);

        app.MapPost($"{ApiBase}/refresh", async (AppDbContext db, HttpContext ctx, RefreshRequest body) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            var reauth = await UserTokens.ReauthAsync(db, body.RefreshToken ?? "", DateTime.UtcNow);
            return reauth is null
                ? Error(ctx, ApiProblem.Unauthorized, "That refresh token is not valid or has expired.")
                : Results.Ok(TokenPairDto.From(reauth.Value.Tokens));
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapPost($"{ApiBase}/logout", async (AppDbContext db, HttpContext ctx, RefreshRequest body) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            await UserTokens.RevokeAsync(db, body.RefreshToken ?? "");

            await UserTokens.RevokeAsync(db, ctx.Request.Cookies[AdminAuth.RefreshCookie] ?? "");
            AdminAuth.ClearCookies(ctx);
            return Results.Ok(new SignedOutDto(true));
        });

        app.MapGet($"{ApiBase}/status", async (AppDbContext db, HttpContext ctx) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            var user = await CurrentAsync(db, ctx);
            return Results.Ok(user is null ? AuthStatusDto.SignedOut : AuthStatusDto.For(user));
        });

        app.MapPost($"{ApiBase}/change_password", async (AppDbContext db, HttpContext ctx, ChangePasswordRequest body) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            var user = await CurrentAsync(db, ctx);
            if (user is null) return Error(ctx, ApiProblem.Unauthorized, "Sign in to continue.");

            var current = body.CurrentPassword ?? "";
            var next = body.NewPassword ?? "";

            if (!LoginGuard.Allowed(LoginGuard.Key($"user:{user.Id}", ctx)))
            {
                ctx.Response.Headers.RetryAfter = "300";
                return Error(ctx, ApiProblem.TooManyRequests, "Too many attempts. Wait a few minutes and try again.");
            }
            if (AccountValidation.PasswordProblem(next) is { } problem)
                return Error(ctx, ApiProblem.Unprocessable, problem);
            if (next == current)
                return Error(ctx, ApiProblem.Unprocessable, "The new password must be different from the current one.");
            if (!AdminAuth.VerifyPassword(current, user.PasswordHash))
            {
                LoginGuard.Failed(LoginGuard.Key($"user:{user.Id}", ctx));
                return Error(ctx, ApiProblem.Forbidden, "The current password is incorrect.");
            }

            LoginGuard.Succeeded(LoginGuard.Key($"user:{user.Id}", ctx));
            user.PasswordHash = AdminAuth.HashPassword(next);
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await UserTokens.RevokeAllAsync(db, user.Id);

            return Results.Ok(TokenPairDto.From(await UserTokens.IssueAsync(db, user, DateTime.UtcNow)));
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapDelete($"{ApiBase}/delete", async (AppDbContext db, HttpContext ctx) =>
        {
            if (!await EnabledAsync(db)) return Results.NotFound();

            var user = await CurrentAsync(db, ctx);
            if (user is null) return Error(ctx, ApiProblem.Unauthorized, "Sign in to continue.");

            if (await db.UserAccounts.CountAsync(a => a.Id != user.Id && !a.IsDisabled) == 0)
                return Error(ctx, ApiProblem.Conflict, "This is the last enabled account and cannot be deleted.");
            if (await AdminEndpoints.IsLastEnabledAdmin(db, user))
                return Error(ctx, ApiProblem.Conflict, "This is the last enabled admin and cannot be deleted.");

            await UserTokens.RevokeAllAsync(db, user.Id);
            db.UserAccounts.Remove(user);
            AdminAuth.ClearCookies(ctx);
            await db.SaveChangesAsync();
            return Results.Ok(new AccountDeletedDto(true));
        }).RequireRateLimiting(RateLimit.Auth);

        app.MapGet(UiBase, Entry);
        app.MapGet($"{UiBase}/{{**rest}}", Entry);

        foreach (var page in new[] { "login", "register", "profile" })
        {
            var name = page;
            app.MapGet($"{UiBase}/{name}", async (AppDbContext db, HttpContext ctx) =>
            {
                var settings = await db.SettingsAsync() ?? new AppSettings();
                if (!Enabled(settings)) return Results.NotFound();
                if (name == "register" && !settings.PublicRegistrationEnabled) return Results.NotFound();

                ctx.Response.Headers.CacheControl = "no-store";
                var html = await File.ReadAllTextAsync(Path.Combine(webRoot, "auth", $"{name}.html"), ctx.RequestAborted);
                html = Signup(html, settings.PublicRegistrationEnabled);

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

    internal static async Task<IResult> LoginAsync(AppDbContext db, HttpContext ctx, LoginRequest body)
    {
        if (!await EnabledAsync(db)) return Results.NotFound();

        var handle = (body.EmailOrUsername ?? "").Trim();
        var password = body.Password ?? "";

        if (!LoginGuard.Allowed(LoginGuard.Key($"user:{handle}", ctx)))
        {
            ctx.Response.Headers.RetryAfter = "300";
            return Error(ctx, ApiProblem.TooManyRequests, "Too many sign-in attempts. Wait a few minutes and try again.");
        }

        var user = await db.UserAccounts.FirstOrDefaultAsync(u =>
            u.Username == handle || (u.Email == handle && handle != ""));

        if (!AdminAuth.CheckPassword(password, user))
        {
            LoginGuard.Failed(LoginGuard.Key($"user:{handle}", ctx));
            AuditLogMiddleware.Note(ctx, $"Failed end-user sign-in as \"{handle}\" with a password");
            return Error(ctx, ApiProblem.Unauthorized, "Incorrect credentials.");
        }

        var now = DateTime.UtcNow;
        var second = Totp.Check(user, body.TotpCode ?? "", now);
        if (second == SecondFactor.Required)
            return ApiProblems.Write(ctx, ApiProblem.Unauthorized, Totp.CodeNeeded, extensions: Totp.RequiredExtension);
        if (second == SecondFactor.Invalid)
        {
            LoginGuard.Failed(LoginGuard.Key($"user:{handle}", ctx));
            AuditLogMiddleware.Note(ctx, $"Failed end-user sign-in as \"{handle}\" with an authenticator code");
            return ApiProblems.Write(ctx, ApiProblem.Unauthorized, "That authenticator code is not valid.", extensions: Totp.RequiredExtension);
        }

        LoginGuard.Succeeded(LoginGuard.Key($"user:{handle}", ctx));
        user.LastLoginAt = now;
        await db.SaveChangesAsync();

        AuditLogMiddleware.Note(ctx, $"End-user sign-in as {user.Username} with a password");
        return Results.Ok(TokenPairDto.From(await UserTokens.IssueAsync(db, user, now)));
    }

    internal static Task<bool> TakenAsync(AppDbContext db, string username, string email, string claimingId) =>
        db.UserAccounts.AnyAsync(u => u.Id != claimingId &&
            (u.Username == username || (email != "" && u.Email == email && u.Role == AccountRoles.User)));

    public static bool Enabled(AppSettings settings) => settings.PublicAuthEnabled;

    public static bool RegistrationOpen(AppSettings settings) => Enabled(settings) && settings.PublicRegistrationEnabled;

    public static bool AnonymousOpen(AppSettings settings) => Enabled(settings) && settings.AnonymousAuthEnabled;

    private static async Task<bool> EnabledAsync(AppDbContext db) =>
        Enabled(await db.SettingsAsync() ?? new AppSettings());

    private static IResult Error(HttpContext ctx, ApiProblem problem, string message) =>
        ApiProblems.Write(ctx, problem, message);
}
