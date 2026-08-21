using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class AuthEndpoints
{
    // Login is the one unauthenticated write, so it includes its own budget: without it the password is guessable at network speed.

    // Paid by every attempt, even when there is no real hash to verify.
    private static readonly string DummyHash = AdminAuth.HashPassword("constant-time-decoy");

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            var username = (body["username"]?.GetValue<string>() ?? "").Trim();
            var password = body["password"]?.GetValue<string>() ?? "";
            var otp = body["otp"]?.GetValue<string>() ?? "";

            var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Username == username);

            // Per-account: rotating IPs keeps the per-IP budget, not this one.
            if (!LoginGuard.Allowed(username))
            {
                ctx.Response.Headers.RetryAfter = "300";
                return Results.Json(new { errors = new[] { "Too many sign-in attempts. Wait a few minutes and try again." } },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            // Same message and the same work either way: a distinct "no such user" reply would confirm which usernames exist.
            var usable = user is not null && !user.IsDisabled;

            // A one-time code is consumed whether or not it matches, so a leaked log line is worth exactly one attempt.
            var ok = otp.Length > 0
                ? usable && OneTimeCodes.Consume(username, otp)
                : usable && !string.IsNullOrEmpty(user!.PasswordHash)
                    ? AdminAuth.VerifyPassword(password, user.PasswordHash)
                    : AdminAuth.VerifyPassword(password, DummyHash); // unknown/disabled: same cost, never true

            var credential = otp.Length > 0 ? "one-time code" : "password";

            if (!ok)
            {
                LoginGuard.Failed(username);
                // The attempt has no session to name the caller by, so the handle that was tried is the only thing that makes the row worth reading. It is a caller's own text, cleaned and capped on the way in.
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

        // Issues a one-time code and prints it to the server log.
        app.MapPost("/api/auth/otp", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            var username = (body["username"]?.GetValue<string>() ?? "").Trim();

            // The same answer whether or not the account exists, so this cannot be used to enumerate usernames.
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
                // Still consumed a slot, so the timing gives nothing away.
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

        // The UI calls this on load to decide between the console and the login screen, so it must stay reachable while signed out.
        app.MapGet("/api/auth/me", async (AppDbContext db, HttpContext ctx) =>
        {
            var user = await AdminAuth.ResolveAsync(db, ctx);
            if (user is null) return Results.Ok(new { authenticated = false });

            return Results.Ok(new { authenticated = true, user.Username, user.Email, user.Role, user.MustChangePassword });
        });

        app.MapPost("/api/auth/password", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            var user = await AdminAuth.ResolveAsync(db, ctx);
            if (user is null) return Results.Unauthorized();

            var current = body["currentPassword"]?.GetValue<string>() ?? "";
            var next = body["newPassword"]?.GetValue<string>() ?? "";

            // Authenticated brute-force target; same lockout as the login form.
            if (!LoginGuard.Allowed(user.Id))
            {
                ctx.Response.Headers.RetryAfter = "300";
                return Results.Json(new { errors = new[] { "Too many attempts. Wait a few minutes and try again." } },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (AccountValidation.PasswordProblem(next) is { } problem)
                return Results.BadRequest(new { errors = new[] { problem } });
            // Reusing the current (seeded) password changes nothing.
            if (next == current)
                return Results.BadRequest(new { errors = new[] { "The new password must be different from the current one." } });

            // Requiring the current password stops a borrowed session from locking the real owner out.
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

            // Every other session was established under the old password.
            await UserTokens.RevokeAllAsync(db, user.Id);
            AdminAuth.IssueCookies(ctx, await UserTokens.IssueAsync(db, user, now));
            return Results.Ok(new { changed = true });
        }).RequireRateLimiting(RateLimit.Auth);
    }
}
