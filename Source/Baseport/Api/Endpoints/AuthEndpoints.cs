using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class AuthEndpoints
{

    private static readonly string DummyHash = AdminAuth.HashPassword("constant-time-decoy");

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            var username = (body["username"]?.GetValue<string>() ?? "").Trim();
            var password = body["password"]?.GetValue<string>() ?? "";
            var otp = body["otp"]?.GetValue<string>() ?? "";

            var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Username == username);

            if (!LoginGuard.Allowed(username))
            {
                ctx.Response.Headers.RetryAfter = "300";
                return Results.Json(new { errors = new[] { "Too many sign-in attempts. Wait a few minutes and try again." } },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var usable = user is not null && !user.IsDisabled;

            var ok = otp.Length > 0
                ? usable && OneTimeCodes.Consume(username, otp)
                : usable && !string.IsNullOrEmpty(user!.PasswordHash)
                    ? AdminAuth.VerifyPassword(password, user.PasswordHash)
                    : AdminAuth.VerifyPassword(password, DummyHash);

            var credential = otp.Length > 0 ? "one-time code" : "password";

            if (!ok)
            {
                LoginGuard.Failed(username);

                AuditLogMiddleware.Note(ctx, $"Failed console sign-in as \"{username}\" with a {credential}");
                return Results.Json(new { errors = new[] { otp.Length > 0 ? "That code is not valid or has expired." : "Incorrect username or password." } },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            LoginGuard.Succeeded(username);
            var now = DateTime.UtcNow;
            user!.LastLoginAt = now;
            await db.SaveChangesAsync();

            AuditLogMiddleware.Note(ctx, $"Console sign-in as {user.Username} with a {credential}");
            AdminAuth.IssueCookies(ctx, await UserTokens.IssueAsync(db, user, now));
            return Results.Ok(new { user.Username, user.Role, user.MustChangePassword });
        }).RequireRateLimiting(RateLimit.Auth);

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

            return Results.Ok(new { authenticated = true, user.Username, user.Email, user.Role, user.MustChangePassword, Avatar = Avatars.DataUri(user.Username) });
        });

        app.MapPost("/api/auth/password", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            var user = await AdminAuth.ResolveAsync(db, ctx);
            if (user is null) return Results.Unauthorized();

            var current = body["currentPassword"]?.GetValue<string>() ?? "";
            var next = body["newPassword"]?.GetValue<string>() ?? "";

            if (!LoginGuard.Allowed(user.Id))
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
                LoginGuard.Failed(user.Id);
                return Results.BadRequest(new { errors = new[] { "The current password is incorrect." } });
            }

            LoginGuard.Succeeded(user.Id);

            var now = DateTime.UtcNow;
            user.PasswordHash = AdminAuth.HashPassword(next);
            user.MustChangePassword = false;
            user.UpdatedAt = now;
            await db.SaveChangesAsync();

            await UserTokens.RevokeAllAsync(db, user.Id);
            AdminAuth.IssueCookies(ctx, await UserTokens.IssueAsync(db, user, now));
            return Results.Ok(new { changed = true });
        }).RequireRateLimiting(RateLimit.Auth);
    }
}
