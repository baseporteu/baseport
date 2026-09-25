using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

public sealed record OidcButton(string Slug, string Name);

public static class OidcEndpoints
{
    private const string Base = "/api/auth/oidc";

    public static void MapOidcEndpoints(this WebApplication app)
    {
        app.MapGet($"{Base}/{{slug}}/start", async (AppDbContext db, HttpContext ctx, string slug, string? surface) =>
        {
            var console = surface != "public";
            if (AdminAuth.NeedsHttps(ctx)) return Results.Redirect(Back(console, OidcFlow.Insecure));
            var provider = await UsableAsync(db, slug, console);
            if (provider is null) return Results.NotFound();

            var settings = await db.SettingsAsync() ?? new AppSettings();
            if (!console && !settings.PublicAuthEnabled) return Results.NotFound();

            try
            {
                var start = await OidcFlow.BeginAsync(provider, RedirectUri(ctx, settings, provider.Slug),
                    console ? "/_/admin" : "/auth/profile", console, ctx.RequestAborted);
                OidcFlow.Bind(ctx, start.State);
                return Results.Redirect(start.AuthorizeUrl);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
            {

                Serilog.Log.Error(ex, "Could not read the discovery document for {Provider}", provider.Slug);
                return Results.Redirect(Back(console, OidcFlow.Failed));
            }
        }).RequireRateLimiting(RateLimit.Oidc);

        app.MapPost($"{Base}/{{slug}}/link", async (AppDbContext db, HttpContext ctx, JsonObject body, string slug) =>
        {
            if (AdminAuth.NeedsHttps(ctx)) return Results.BadRequest(new { errors = new[] { AdminAuth.HttpsRequired } });
            if (await AdminAuth.ResolveAsync(db, ctx) is not { } user)
                return Results.Json(new { errors = new[] { "Sign in to continue." } }, statusCode: 401);

            var provider = await UsableAsync(db, slug, console: true);
            if (provider is null) return Results.NotFound();

            if (!LoginGuard.Allowed(LoginGuard.Key($"user:{user.Id}", ctx)))
            {
                ctx.Response.Headers.RetryAfter = "300";
                return Results.Json(new { errors = new[] { "Too many attempts. Wait a few minutes and try again." } }, statusCode: 429);
            }

            var password = body["currentPassword"] is JsonValue pv && pv.TryGetValue<string>(out var typed) ? typed : "";
            if (!AdminAuth.VerifyPassword(password, user.PasswordHash))
            {
                LoginGuard.Failed(LoginGuard.Key($"user:{user.Id}", ctx));
                AuditLogMiddleware.Note(ctx, $"Failed link attempt for {user.Username} through {provider.Name}");
                return Results.BadRequest(new { errors = new[] { "That password is incorrect." } });
            }
            LoginGuard.Succeeded(LoginGuard.Key($"user:{user.Id}", ctx));

            try
            {
                var settings = await db.SettingsAsync() ?? new AppSettings();
                var start = await OidcFlow.BeginAsync(provider, RedirectUri(ctx, settings, provider.Slug),
                    "/_/admin/settings/auth", console: true, ctx.RequestAborted, linkTo: user.Id);
                OidcFlow.Bind(ctx, start.State);
                return Results.Ok(new { authorizeUrl = start.AuthorizeUrl });
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
            {
                Serilog.Log.Error(ex, "Could not read the discovery document for {Provider}", provider.Slug);
                return Results.BadRequest(new { errors = new[] { "That provider could not be reached. Check its issuer URL." } });
            }
        }).RequireRateLimiting(RateLimit.Oidc);

        app.MapGet($"{Base}/{{slug}}/callback", async (AppDbContext db, HttpContext ctx, IHttpClientFactory clients,
            string slug, string? code, string? state, string? error) =>
        {

            var flow = OidcFlow.Claim(state, ctx.Request.Cookies[OidcFlow.BindingCookie]);
            ctx.Response.Cookies.Delete(OidcFlow.BindingCookie, new CookieOptions { Path = "/api/auth/oidc" });

            if (flow is null)
                return Results.Redirect(Back(await db.OidcProviders.AnyAsync(p => p.Slug == slug && p.ConsoleEnabled), OidcFlow.Failed));

            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            {
                AuditLogMiddleware.Note(ctx, $"Cancelled {Door(flow.Console)} sign-in at {slug}");
                return Results.Redirect(Back(flow.Console, OidcFlow.Denied));
            }

            var provider = await UsableAsync(db, slug, flow.Console);
            if (provider is null || provider.Id != flow.ProviderId)
                return Results.Redirect(Back(flow.Console, OidcFlow.Failed));

            if (!flow.Console && !(await db.SettingsAsync() ?? new AppSettings()).PublicAuthEnabled)
                return Results.NotFound();

            OidcIdentity? identity;
            try
            {
                identity = await OidcFlow.CompleteAsync(provider, flow, code, clients.CreateClient(ProxyTarget.OidcClient), ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
            {
                Serilog.Log.Error(ex, "Could not complete the sign-in with {Provider}", provider.Slug);
                identity = null;
            }
            if (identity is null) return Results.Redirect(Back(flow.Console, OidcFlow.Failed));

            if (flow.LinkTo.Length > 0)
                return await CompleteLinkAsync(db, ctx, provider, flow, identity);

            var (user, problem) = await ResolveAccountAsync(db, provider, identity);
            if (user is null)
            {

                AuditLogMiddleware.Note(ctx, $"Refused {Door(flow.Console)} sign-in through {provider.Name} ({problem})");
                return Results.Redirect(Back(flow.Console, problem));
            }

            if (flow.Console && user.Role != AccountRoles.Admin)
            {
                AuditLogMiddleware.Note(ctx, $"Refused console sign-in as {user.Username} through {provider.Name} (no console access)");
                return Results.Redirect(Back(true, OidcFlow.NoConsole));
            }

            var now = DateTime.UtcNow;
            user.LastLoginAt = now;

            user.MustChangePassword = false;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                Serilog.Log.Warning(ex, "Could not link {Username} to {Provider}: the identity is already held by another account.", user.Username, provider.Slug);
                return Results.Redirect(Back(flow.Console, OidcFlow.Failed));
            }

            AuditLogMiddleware.Note(ctx, $"{Door(flow.Console)} sign-in as {user.Username} through {provider.Name}");
            AdminAuth.IssueCookies(ctx, await UserTokens.IssueAsync(db, user, now));
            return Results.Redirect(flow.ReturnTo);
        }).RequireRateLimiting(RateLimit.Oidc);

        app.MapGet("/api/_admin/oidc-providers", async (AppDbContext db, HttpContext ctx) =>
        {
            var settings = await db.SettingsAsync() ?? new AppSettings();
            var providers = await db.OidcProviders.OrderBy(p => p.Position).ThenBy(p => p.Name).ToListAsync();
            return Results.Ok(providers.Select(p => Dto(p, ctx, settings)));
        });

        app.MapPost("/api/_admin/oidc-providers", async (AppDbContext db, HttpContext ctx, JsonObject body) =>
        {
            var provider = new OidcProvider { Id = Ids.NewShortId(12), CreatedAt = DateTime.UtcNow };
            if (await ApplyAsync(db, provider, body, ctx.RequestAborted) is { } problem)
                return Results.BadRequest(new { errors = new[] { problem } });

            db.OidcProviders.Add(provider);
            await db.SaveChangesAsync();
            return Results.Ok(Dto(provider, ctx, await db.SettingsAsync() ?? new AppSettings()));
        });

        app.MapPatch("/api/_admin/oidc-providers/{id}", async (AppDbContext db, HttpContext ctx, string id, JsonObject body) =>
        {
            var provider = await db.OidcProviders.FirstOrDefaultAsync(p => p.Id == id);
            if (provider is null) return Results.NotFound();

            if (await ApplyAsync(db, provider, body, ctx.RequestAborted) is { } problem)
                return Results.BadRequest(new { errors = new[] { problem } });

            await db.SaveChangesAsync();

            OidcFlow.Forget(provider.Id);
            return Results.Ok(Dto(provider, ctx, await db.SettingsAsync() ?? new AppSettings()));
        });

        app.MapDelete("/api/_admin/oidc-providers/{id}", async (AppDbContext db, string id) =>
        {
            var provider = await db.OidcProviders.FirstOrDefaultAsync(p => p.Id == id);
            if (provider is null) return Results.NotFound();

            await db.UserAccounts.Where(u => u.OidcProviderId == id)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.OidcProviderId, "").SetProperty(a => a.OidcSubject, ""));
            db.OidcProviders.Remove(provider);
            await db.SaveChangesAsync();
            OidcFlow.Forget(id);
            return Results.Ok(new { deleted = true });
        });
    }

    private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]{1,31}$", RegexOptions.Compiled);

    private static object Dto(OidcProvider p, HttpContext ctx, AppSettings settings) => new
    {
        p.Id, p.Slug, p.Name, p.Authority, p.ClientId, p.Scopes,
        p.UsernameClaim, p.EmailClaim,
        p.IsEnabled, p.ConsoleEnabled, p.PublicEnabled, p.CreateAccounts, p.Position,
        p.CreatedAt, p.UpdatedAt,

        HasClientSecret = p.ClientSecret.Length > 0,
        RedirectUri = RedirectUri(ctx, settings, p.Slug)
    };

    internal static async Task<string?> ApplyAsync(AppDbContext db, OidcProvider provider, JsonObject body, CancellationToken token)
    {
        var slug = Text(body, "slug", provider.Slug).Trim().ToLowerInvariant();
        var name = Text(body, "name", provider.Name).Trim();
        var authority = Text(body, "authority", provider.Authority).Trim().TrimEnd('/');
        var clientId = Text(body, "clientId", provider.ClientId).Trim();

        if (!SlugPattern.IsMatch(slug))
            return "The key must be 2 to 32 characters of lowercase letters, digits and hyphens.";
        if (await db.OidcProviders.AnyAsync(p => p.Slug == slug && p.Id != provider.Id, token))
            return "Another provider already uses that key.";
        if (name.Length is 0 or > 64) return "The name must be 1 to 64 characters.";
        if (clientId.Length is 0 or > 256) return "A client ID is required.";

        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "The issuer URL must be an absolute http or https address.";
        if (uri.Scheme == "http" && !OidcFlow.AllowsPlainHttp(authority))
            return "Only a provider on this machine may be reached over plain http. Use https.";

        var scopes = Text(body, "scopes", provider.Scopes).Trim() is { Length: > 0 } sc ? sc : "openid profile email";
        if (!scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid"))
            return "The scopes must include openid, or the provider returns no id_token.";

        var enabled = Flag(body, "isEnabled", provider.IsEnabled);
        if (enabled && !Flag(body, "consoleEnabled", provider.ConsoleEnabled) && !Flag(body, "publicEnabled", provider.PublicEnabled))
            return "An enabled provider must be offered on at least one sign-in screen. Turn one on, or switch the provider off to park it.";

        OidcFlow.Forget(provider.Id);
        try
        {
            var document = await OidcFlow.DocumentAsync(new OidcProvider { Id = provider.Id, Authority = authority }, token);
            if (string.IsNullOrEmpty(document.AuthorizationEndpoint) || string.IsNullOrEmpty(document.TokenEndpoint))
                return $"{OidcFlow.MetadataAddress(authority)} does not declare an authorization and token endpoint.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Serilog.Log.Warning(ex, "Discovery failed for {Authority}", authority);
            return $"Could not read {OidcFlow.MetadataAddress(authority)}. Check the issuer URL and that this instance can reach it.";
        }

        provider.Slug = slug;
        provider.Name = name;
        provider.Authority = authority;
        provider.ClientId = clientId;
        provider.Scopes = scopes;

        if (body["clientSecret"] is JsonValue sv && sv.TryGetValue<string>(out var secret))
            provider.ClientSecret = secret.Trim();
        provider.UsernameClaim = Text(body, "usernameClaim", provider.UsernameClaim).Trim() is { Length: > 0 } uc ? uc : "preferred_username";
        provider.EmailClaim = Text(body, "emailClaim", provider.EmailClaim).Trim() is { Length: > 0 } ec ? ec : "email";
        provider.IsEnabled = enabled;
        provider.ConsoleEnabled = Flag(body, "consoleEnabled", provider.ConsoleEnabled);
        provider.PublicEnabled = Flag(body, "publicEnabled", provider.PublicEnabled);
        provider.CreateAccounts = Flag(body, "createAccounts", provider.CreateAccounts);
        if (body["position"] is JsonValue pv && pv.TryGetValue<int>(out var position)) provider.Position = position;
        provider.UpdatedAt = DateTime.UtcNow;

        return null;
    }

    private static string Text(JsonObject body, string name, string fallback) =>
        body[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    private static bool Flag(JsonObject body, string name, bool fallback) =>
        body[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static string Door(bool console) => console ? "Console" : "End-user";

    public static Task<List<OidcButton>> OfferedAsync(AppDbContext db, bool console) =>
        db.OidcProviders
            .Where(p => p.IsEnabled && (console ? p.ConsoleEnabled : p.PublicEnabled))
            .OrderBy(p => p.Position).ThenBy(p => p.Name)
            .Select(p => new OidcButton(p.Slug, p.Name))
            .ToListAsync();

    public static string RedirectUri(HttpContext ctx, AppSettings settings, string slug)
    {
        var origin = string.IsNullOrWhiteSpace(settings.SiteUrl)
            ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
            : settings.SiteUrl.Trim().TrimEnd('/');
        return $"{origin}{Base}/{slug}/callback";
    }

    private static Task<OidcProvider?> UsableAsync(AppDbContext db, string slug, bool console) =>
        db.OidcProviders.FirstOrDefaultAsync(p =>
            p.Slug == slug && p.IsEnabled && (console ? p.ConsoleEnabled : p.PublicEnabled));

    internal static async Task<(UserAccount? User, string Problem)> ResolveAccountAsync(AppDbContext db, OidcProvider provider, OidcIdentity identity)
    {
        var user = await db.UserAccounts.FirstOrDefaultAsync(u =>
            u.OidcProviderId == provider.Id && u.OidcSubject == identity.Subject);

        if (user is null && identity.EmailVerified && identity.Email.Length > 0)
            user = await Linkable(db, u => u.Email == identity.Email);

        if (user is not null && user.OidcSubject.Length == 0)
        {
            user.OidcProviderId = provider.Id;
            user.OidcSubject = identity.Subject;
            user.UpdatedAt = DateTime.UtcNow;
        }

        if (user is null)
        {

            if (await AdminNeverLinksNoteAsync(db, provider, identity) is { Length: > 0 } adminNote)
                Serilog.Log.Warning("{Note}", adminNote);

            if (!provider.CreateAccounts)
            {

                var command = $"{AccountsCli.Invocation()} accounts link <username> {provider.Slug} {identity.Subject}";
                Serilog.Log.Warning("{Provider} sign-in failed: Subject {Subject} ({PresentedAs}) is not linked to a Baseport account. " +
                    "Link it manually using: {Command}",
                    provider.Name, identity.Subject,
                    identity.Username is { Length: > 0 } ? identity.Username : "no name claim", command);
                Serilog.Log.Warning("{Note}", await WhyNoEmailMatchAsync(db, provider, identity));
                return (null, OidcFlow.NoAccount);
            }
            user = await ProvisionAsync(db, provider, identity);
        }

        return user.IsDisabled ? (null, OidcFlow.Disabled) : (user, "");
    }

    internal static async Task<string> WhyNoEmailMatchAsync(AppDbContext db, OidcProvider provider, OidcIdentity identity)
    {
        if (identity.Email.Length == 0)
            return $"{provider.Name} sent no {provider.EmailClaim} claim, matching by e-mail was skipped.";

        if (!identity.EmailVerified)
            return $"{provider.Name} did not mark {identity.Email} as verified, matching by e-mail was skipped. " +
                "An unverified address is a claim to somebody else's account.";

        var holder = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == identity.Email && u.Email != "");
        if (holder is null)
            return $"No Baseport account includes the e-mail {identity.Email}. Put it on the non-admin account without a password you want matched, and the next sign-in links itself.";

        if (holder.Role == AccountRoles.Admin)
            return $"{holder.Username} includes {identity.Email} but is an admin, and an admin is never linked automatically: " +
                "console access must not follow from a name in somebody else's directory. Link it by hand, once.";

        if (holder.PasswordHash.Length > 0)
            return $"{holder.Username} includes {identity.Email} but has a password, and an account with a password is never linked by e-mail: " +
                "whoever registered it may not own the address. Link it by hand, once.";

        return $"{holder.Username} includes {identity.Email} but is already linked to another provider identity. Unlink it first.";
    }

    internal static async Task<string> AdminNeverLinksNoteAsync(AppDbContext db, OidcProvider provider, OidcIdentity identity)
    {
        if (!await db.UserAccounts.AnyAsync(u => u.Role == AccountRoles.Admin && u.OidcSubject == "")) return "";

        var command = $"{AccountsCli.Invocation()} accounts link <username> {provider.Slug} {identity.Subject}";
        return "An admin account is never linked by a claim, whatever the provider sends: console access must not follow from a name in somebody else's directory. " +
            $"If the account you mean is an admin, sign in to the console and use Link my account under Settings > Authentication, or run: {command}";
    }

    private static Task<UserAccount?> Linkable(AppDbContext db, System.Linq.Expressions.Expression<Func<UserAccount, bool>> match) =>
        db.UserAccounts
            .Where(u => u.OidcSubject == "" && u.Role != AccountRoles.Admin && u.PasswordHash == "")
            .Where(match)
            .FirstOrDefaultAsync();

    private static async Task<UserAccount> ProvisionAsync(AppDbContext db, OidcProvider provider, OidcIdentity identity)
    {
        var username = identity.Username;
        if (AccountValidation.Validate(username, "").Count > 0 || await db.UserAccounts.AnyAsync(u => u.Username == username))
            username = UserAuthEndpoints.DeriveUsername(identity.Email.Length > 0 ? identity.Email : identity.Subject);

        var email = AccountValidation.IsEmail(identity.Email) ? identity.Email : "";
        var now = DateTime.UtcNow;
        var user = new UserAccount
        {
            Id = Ids.NewShortId(12),
            Username = username,
            Email = email,

            Role = AccountRoles.User,
            OidcProviderId = provider.Id,
            OidcSubject = identity.Subject,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.UserAccounts.Add(user);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            db.Entry(user).State = EntityState.Detached;
            var linked = await db.UserAccounts.FirstOrDefaultAsync(u =>
                u.OidcProviderId == provider.Id && u.OidcSubject == identity.Subject);
            if (linked is null) throw;
            return linked;
        }
        Serilog.Log.Information("Created account {Username} on first sign-in through {Provider}.", username, provider.Slug);
        return user;
    }

    internal static async Task<string?> LinkRefusalAsync(AppDbContext db, OidcProvider provider, OidcFlow.PendingFlow flow, OidcIdentity identity, string sessionUserId)
    {
        if (flow.LinkTo.Length == 0) return "that flow was a sign-in, not a link";
        if (sessionUserId.Length == 0) return "there is no signed-in account to link";
        if (sessionUserId != flow.LinkTo) return "the session no longer stores the account that started it";

        return await db.UserAccounts.AnyAsync(a => a.OidcProviderId == provider.Id && a.OidcSubject == identity.Subject && a.Id != flow.LinkTo)
            ? "that identity is already held by another account"
            : null;
    }

    private static async Task<IResult> CompleteLinkAsync(AppDbContext db, HttpContext ctx, OidcProvider provider, OidcFlow.PendingFlow flow, OidcIdentity identity)
    {
        var user = await AdminAuth.ResolveAsync(db, ctx);
        if (await LinkRefusalAsync(db, provider, flow, identity, user?.Id ?? "") is { } refusal)
        {
            AuditLogMiddleware.Note(ctx, $"Refused a link through {provider.Name}: {refusal}");
            return Results.Redirect(Back(true, OidcFlow.NotLinked));
        }

        user!.OidcProviderId = provider.Id;
        user.OidcSubject = identity.Subject;
        user.UpdatedAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            Serilog.Log.Warning(ex, "Could not link {Username} to {Provider}: the identity is already held by another account.", user.Username, provider.Slug);
            return Results.Redirect(Back(true, OidcFlow.NotLinked));
        }

        await UserTokens.RevokeAllAsync(db, user.Id);
        AdminAuth.IssueCookies(ctx, await UserTokens.IssueAsync(db, user, DateTime.UtcNow));

        AuditLogMiddleware.Note(ctx, $"{user.Username} linked their account to {provider.Name}");
        return Results.Redirect($"{flow.ReturnTo}?sso={OidcFlow.Linked}");
    }

    private static string Back(bool console, string problem) =>
        console ? $"/_/auth?sso={problem}" : $"/auth/login?sso={problem}";
}
