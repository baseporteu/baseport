using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

// The two browser stops of an OpenID Connect sign-in. Both doors of pillar 12 end in the same place: the account is re-read from _users, the same ES256 pair is minted, and the same two cookies are set. Which provider a caller came through decides nothing afterwards.
public sealed record OidcButton(string Slug, string Name);

public static class OidcEndpoints
{
    private const string Base = "/api/auth/oidc";

    public static void MapOidcEndpoints(this WebApplication app)
    {
        app.MapGet($"{Base}/{{slug}}/start", async (AppDbContext db, HttpContext ctx, string slug, string? surface) =>
        {
            var console = surface != "public";
            var provider = await UsableAsync(db, slug, console);
            if (provider is null) return Results.NotFound();

            var settings = await db.SettingsAsync() ?? new AppSettings();
            if (!console && !settings.PublicAuthEnabled) return Results.NotFound();

            try
            {
                var start = await OidcFlow.BeginAsync(provider, RedirectUri(ctx, settings, provider.Slug),
                    console ? "/_/admin" : "/auth/profile", console, ctx.RequestAborted);
                return Results.Redirect(start.AuthorizeUrl);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
            {
                // A provider that is down or misconfigured is an operator problem, not a visitor's: name it in the log and send them back to a working sign-in screen.
                Serilog.Log.Error(ex, "Could not read the discovery document for {Provider}", provider.Slug);
                return Results.Redirect(Back(console, OidcFlow.Failed));
            }
        }).RequireRateLimiting(RateLimit.Oidc);

        // An account binding a provider identity to itself. Pillar 17 refuses to let a claim choose an account, and this does not: the account is fixed here, from a session that is already authenticated, before the redirect is built. What comes back from the provider is written, never matched, so `Linkable` and the admin rule behind it are untouched.
        // The password is asked for the same reason /api/auth/password asks: a borrowed session must not be able to bolt a second way in onto somebody else's account.
        app.MapPost($"{Base}/{{slug}}/link", async (AppDbContext db, HttpContext ctx, JsonObject body, string slug) =>
        {
            if (await AdminAuth.ResolveAsync(db, ctx) is not { } user)
                return Results.Json(new { errors = new[] { "Sign in to continue." } }, statusCode: 401);

            var provider = await UsableAsync(db, slug, console: true);
            if (provider is null) return Results.NotFound();

            if (!LoginGuard.Allowed($"user:{user.Id}"))
            {
                ctx.Response.Headers.RetryAfter = "300";
                return Results.Json(new { errors = new[] { "Too many attempts. Wait a few minutes and try again." } }, statusCode: 429);
            }

            var password = body["currentPassword"] is JsonValue pv && pv.TryGetValue<string>(out var typed) ? typed : "";
            if (!AdminAuth.VerifyPassword(password, user.PasswordHash))
            {
                LoginGuard.Failed($"user:{user.Id}");
                AuditLogMiddleware.Note(ctx, $"Failed link attempt for {user.Username} through {provider.Name}");
                return Results.BadRequest(new { errors = new[] { "That password is incorrect." } });
            }
            LoginGuard.Succeeded($"user:{user.Id}");

            try
            {
                var settings = await db.SettingsAsync() ?? new AppSettings();
                var start = await OidcFlow.BeginAsync(provider, RedirectUri(ctx, settings, provider.Slug),
                    "/_/admin/settings/auth", console: true, ctx.RequestAborted, linkTo: user.Id);
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
            // The state is spent here whatever happens next, so a code cannot be presented twice.
            var flow = OidcFlow.Claim(state);
            // Without a flow there is nothing that says which door this was, so the provider's own surfaces decide where a stale callback lands.
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

            // Re-read, not carried in the flow: every other route on the public surface answers 404 the moment the switch is off, and a sign-in already in the air must not be the one exception that still mints a token.
            if (!flow.Console && !(await db.SettingsAsync() ?? new AppSettings()).PublicAuthEnabled)
                return Results.NotFound();

            OidcIdentity? identity;
            try
            {
                identity = await OidcFlow.CompleteAsync(provider, flow, code, clients.CreateClient(), ctx.RequestAborted);
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
                // The provider name only, never the subject or the token: a log an operator reads should say which door was tried, not carry the credential that tried it.
                AuditLogMiddleware.Note(ctx, $"Refused {Door(flow.Console)} sign-in through {provider.Name} ({problem})");
                return Results.Redirect(Back(flow.Console, problem));
            }

            // A console session needs console access; without this check the shell renders and then refuses every request it makes.
            if (flow.Console && user.Role != AccountRoles.Admin)
            {
                AuditLogMiddleware.Note(ctx, $"Refused console sign-in as {user.Username} through {provider.Name} (no console access)");
                return Results.Redirect(Back(true, OidcFlow.NoConsole));
            }

            var now = DateTime.UtcNow;
            user.LastLoginAt = now;
            // The flag guards a seeded password that is no longer the credential in use, and the change screen would ask for a password this account may never have had.
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
            // A rotated secret or a moved authority must not be served out of the document cache.
            OidcFlow.Forget(provider.Id);
            return Results.Ok(Dto(provider, ctx, await db.SettingsAsync() ?? new AppSettings()));
        });

        app.MapDelete("/api/_admin/oidc-providers/{id}", async (AppDbContext db, string id) =>
        {
            var provider = await db.OidcProviders.FirstOrDefaultAsync(p => p.Id == id);
            if (provider is null) return Results.NotFound();

            // The accounts linked to it keep their history and fall back to their password, if they have one.
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
        // Write-only: what the console needs to know is whether one is set, never what it is.
        HasClientSecret = p.ClientSecret.Length > 0,
        RedirectUri = RedirectUri(ctx, settings, p.Slug)
    };

    // Validated when saved rather than when called, the same way an access rule is: a provider that cannot be reached would otherwise be a failed sign-in with nothing on screen to explain it.
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

        // A provider that is on but offered on neither screen configures nothing and shows nothing, which reads as a broken save rather than a refused one. Switch it off to park it instead; the surfaces are remembered.
        var enabled = Flag(body, "isEnabled", provider.IsEnabled);
        if (enabled && !Flag(body, "consoleEnabled", provider.ConsoleEnabled) && !Flag(body, "publicEnabled", provider.PublicEnabled))
            return "An enabled provider must be offered on at least one sign-in screen. Turn one on, or switch the provider off to park it.";

        // Reaching the provider is the check: a typo in the issuer URL is the single most common way this is misconfigured, and it shows up here instead of as a dead button. Probed on a throwaway, because nothing may be written to the tracked entity until every check has passed.
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
        // An absent key leaves the stored secret alone, so an edit that does not retype it does not clear it; an empty string does clear it, which is how a confidential client becomes a public one.
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

    // The providers a sign-in screen offers, rendered into its bootstrap payload rather than fetched: the screen paints without a round trip, and there is one place that decides what a surface may show.
    public static Task<List<OidcButton>> OfferedAsync(AppDbContext db, bool console) =>
        db.OidcProviders
            .Where(p => p.IsEnabled && (console ? p.ConsoleEnabled : p.PublicEnabled))
            .OrderBy(p => p.Position).ThenBy(p => p.Name)
            .Select(p => new OidcButton(p.Slug, p.Name))
            .ToListAsync();

    // Registered at the provider verbatim, so it is built from the configured site URL when there is one and never from a header a caller controls.
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

    // Match on the subject, then link, then provision. A username is reassignable at the provider and a subject is not, so the subject is what a returning caller is found by; the other two only ever run once per account.
    internal static async Task<(UserAccount? User, string Problem)> ResolveAccountAsync(AppDbContext db, OidcProvider provider, OidcIdentity identity)
    {
        var user = await db.UserAccounts.FirstOrDefaultAsync(u =>
            u.OidcProviderId == provider.Id && u.OidcSubject == identity.Subject);

        if (user is null && identity.Username.Length > 0)
            user = await Linkable(db, u => u.Username == identity.Username);

        // Only when the provider says it verified the address: an unverified e-mail claim is a claim to somebody else's account.
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
            // Above the branch on purpose. With CreateAccounts on, an admin who tried to sign in is passed over for a fresh plain account and the refusal an operator would have read never happens, which is the same confusion with none of the explanation.
            if (await AdminNeverLinksNoteAsync(db, provider, identity) is { Length: > 0 } adminNote)
                Serilog.Log.Warning("{Note}", adminNote);

            if (!provider.CreateAccounts)
            {
                // The subject is the only handle `baseport accounts link` takes, and a refused sign-in is the one place it is ever seen. The command is built here rather than templated, so no property is repeated: Serilog binds positionally, and a name repeated in the template silently shifts every value after it.
                var command = $"{AccountsCli.Invocation()} accounts link <username> {provider.Slug} {identity.Subject}";
                Serilog.Log.Warning("{Provider} sign-in failed: Subject {Subject} ({PresentedAs}) is not linked to a Baseport account. " +
                    "Link it manually using: {Command}",
                    provider.Name, identity.Subject,
                    identity.Username is { Length: > 0 } ? identity.Username : "no name claim", command);
                if (UnusableNameNote(provider, identity) is { Length: > 0 } note) Serilog.Log.Warning("{Note}", note);
                Serilog.Log.Warning("{Note}", await WhyNoEmailMatchAsync(db, provider, identity));
                return (null, OidcFlow.NoAccount);
            }
            user = await ProvisionAsync(db, provider, identity);
        }

        return user.IsDisabled ? (null, OidcFlow.Disabled) : (user, "");
    }

    // Matching on a verified e-mail is not a setting, it always runs. When it finds nothing there are four different reasons, and a refusal that names none of them sends an operator to audit the wrong thing: the account list, the claim mapping, the provider's verification, or pillar 17's admin rule.
    internal static async Task<string> WhyNoEmailMatchAsync(AppDbContext db, OidcProvider provider, OidcIdentity identity)
    {
        if (identity.Email.Length == 0)
            return $"{provider.Name} sent no {provider.EmailClaim} claim, so matching by e-mail was skipped.";

        if (!identity.EmailVerified)
            return $"{provider.Name} did not mark {identity.Email} as verified, so matching by e-mail was skipped. " +
                "An unverified address is a claim to somebody else's account.";

        var holder = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == identity.Email && u.Email != "");
        if (holder is null)
            return $"No Baseport account carries the e-mail {identity.Email}. Put it on the non-admin account you want matched, and the next sign-in links itself.";

        if (holder.Role == AccountRoles.Admin)
            return $"{holder.Username} carries {identity.Email} but is an admin, and an admin is never linked automatically: " +
                "console access must not follow from a name in somebody else's directory. Link it by hand, once.";

        return $"{holder.Username} carries {identity.Email} but is already linked to another provider identity. Unlink it first.";
    }

    // Both claim paths run through `Linkable`, which filters admins out, so no claim will ever link an admin however it is spelled. The other two notes read as though fixing the claim or the e-mail would help, and for the operator bootstrapping their own console it never can. Only emitted where it applies: an instance whose admins are all linked already has nothing to explain.
    internal static async Task<string> AdminNeverLinksNoteAsync(AppDbContext db, OidcProvider provider, OidcIdentity identity)
    {
        if (!await db.UserAccounts.AnyAsync(u => u.Role == AccountRoles.Admin && u.OidcSubject == "")) return "";

        var command = $"{AccountsCli.Invocation()} accounts link <username> {provider.Slug} {identity.Subject}";
        return "An admin account is never linked by a claim, whatever the provider sends: console access must not follow from a name in somebody else's directory. " +
            $"If the account you mean is an admin, sign in to the console and use Link my account under Settings > Authentication, or run: {command}";
    }

    // A name claim that could never be a Baseport username matches nothing and says nothing, which leaves an operator auditing their account list when the claim is what is wrong. Pocket ID sending an address as preferred_username is the common way in.
    internal static string UnusableNameNote(OidcProvider provider, OidcIdentity identity)
    {
        if (identity.Username.Length == 0) return "";
        if (AccountValidation.Validate(identity.Username, "").Count == 0) return "";

        return $"{provider.Name} {provider.UsernameClaim} claim \"{identity.Username}\" is invalid for Baseport. " +
            "Auto-matching skipped. Link manually, or update the provider's claim to a plain username that a non-admin account carries.";
    }

    // An account already tied to another provider identity is not a candidate: linking it would move it.
    // An admin is never a candidate either, whichever claim matched. Auto-linking hands an account to whoever the provider says holds that name, and for an admin that is console access granted by a username at somebody else's directory. Pillar 16 puts admin accounts out of reach of anything but the shell, so `baseport accounts link` is the only way one gets a provider identity.
    private static Task<UserAccount?> Linkable(AppDbContext db, System.Linq.Expressions.Expression<Func<UserAccount, bool>> match) =>
        db.UserAccounts
            .Where(u => u.OidcSubject == "" && u.Role != AccountRoles.Admin)
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
            // Never admin, the same floor registration keeps: console access is granted deliberately, from the shell.
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

    // A code, never a message: nothing the provider or a caller wrote reaches the address bar, and the screen owns the wording.
    // The whole decision, kept out of the handler so it can be tested without a browser. Two refusals, and the first is the one that matters: the session presenting the callback must be the same account that started the flow, or a link begun in one operator's browser could be finished in another's.
    internal static async Task<string?> LinkRefusalAsync(AppDbContext db, OidcProvider provider, OidcFlow.PendingFlow flow, OidcIdentity identity, string sessionUserId)
    {
        if (flow.LinkTo.Length == 0) return "that flow was a sign-in, not a link";
        if (sessionUserId.Length == 0) return "there is no signed-in account to link";
        if (sessionUserId != flow.LinkTo) return "the session no longer holds the account that started it";

        // One provider identity maps to at most one account, the same floor `baseport accounts link` keeps.
        return await db.UserAccounts.AnyAsync(a => a.OidcProviderId == provider.Id && a.OidcSubject == identity.Subject && a.Id != flow.LinkTo)
            ? "that identity is already held by another account"
            : null;
    }

    // The account is re-resolved from the session rather than trusted from the flow: a link that started in one operator's browser must not finish in another's. The subject is the only thing taken from the provider, and it is written to that account, never used to find one.
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

        // A second way into the account is a change of credentials, so every session opened before it is done with, exactly as the CLI does it. The one being used right now is reissued instead of dropped, the way a password change does, or linking would sign the operator out of the screen they did it from.
        await UserTokens.RevokeAllAsync(db, user.Id);
        AdminAuth.IssueCookies(ctx, await UserTokens.IssueAsync(db, user, DateTime.UtcNow));

        AuditLogMiddleware.Note(ctx, $"{user.Username} linked their account to {provider.Name}");
        return Results.Redirect($"{flow.ReturnTo}?sso={OidcFlow.Linked}");
    }

    private static string Back(bool console, string problem) =>
        console ? $"/_/auth?sso={problem}" : $"/auth/login?sso={problem}";
}
