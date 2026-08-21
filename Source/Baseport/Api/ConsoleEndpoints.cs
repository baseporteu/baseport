using Microsoft.EntityFrameworkCore;

namespace Baseport;

// Serves the admin console. Two jobs, both about doing work here instead of in the browser: 1.
public static class ConsoleEndpoints
{
    // The console owns a prefix, not the site root, hosted forms and embeds have somewhere to live and "is this admin?" is a path test.
    private const string Base = "/_/admin";

    // The login card lives on its own page so a signed-out visitor never loads the shell, the sidebar or any console script.
    private const string Auth = "/_/auth";

    // Order matters: the shell opens <html> and <main>, the footer closes them.
    private static readonly string[] Parts =
    {
        "admin/_shell.html",
        "admin/views/tables.html",
        "admin/views/forms.html",
        "admin/views/sql.html",
        "admin/views/schema.html",
        "admin/views/auth.html",
        "admin/views/logs.html",
        "admin/views/settings.html",
        "admin/_footer.html"
    };

    private static readonly string[] AuthParts = { "admin/_auth.html" };

    public static void MapConsoleEndpoints(this WebApplication app)
    {
        var webRoot = app.Environment.WebRootPath;

        // Deep links render the same document; the client router reads the path.
        app.MapGet(Base, (AppDbContext db, HttpContext ctx) => RenderAsync(db, ctx, webRoot, authPage: false));
        app.MapGet($"{Base}/{{**rest}}", (AppDbContext db, HttpContext ctx) => RenderAsync(db, ctx, webRoot, authPage: false));
        app.MapGet(Auth, (AppDbContext db, HttpContext ctx) => RenderAsync(db, ctx, webRoot, authPage: true));

        // anonymous for the same reason as the document it renders
        app.MapGet("/docs", () => Results.File(Path.Combine(webRoot, "docs.html"), "text/html; charset=utf-8"));

        // An unmatched /api path answers as an API: returning HTML with a 200 would tell a client its request succeeded.
        app.MapFallback(async (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                await ctx.Response.WriteAsJsonAsync(new { errors = new[] { "No such endpoint." } });
        });
    }

    private static async Task RenderAsync(AppDbContext db, HttpContext ctx, string webRoot, bool authPage)
    {
        // Anonymous visitors never load the console, signed-in visitors are never asked to sign in again, and a session still on the one-time password is forced back to the change screen.
        var user = await AdminAuth.ResolveAsync(db, ctx);
        var signedIn = user is not null;
        var mustChange = signedIn && user!.MustChangePassword;
        if (signedIn && !mustChange)
        {
            if (authPage)
            {
                ctx.Response.Redirect(Base);
                return;
            }
        }
        else if (!authPage)
        {
            ctx.Response.Redirect(Auth);
            return;
        }

        ctx.Response.ContentType = "text/html; charset=utf-8";
        // The payload is per-session, a shared cache must never serve one user's bootstrap to another.
        ctx.Response.Headers.CacheControl = "no-store";

        var token = ctx.RequestAborted;

        // Resolving the caller can remint an expired auth cookie, and a cookie is a header: it has to happen before the first byte of the body goes out, not halfway through the stream.
        var bootstrap = await BootstrapAsync(db, ctx, authPage);

        var parts = authPage ? AuthParts : Parts;
        foreach (var part in parts)
        {
            var html = await File.ReadAllTextAsync(Path.Combine(webRoot, part), token);
            // The bootstrap goes immediately before the scripts that read it.
            if (!authPage && part == "admin/_shell.html")
            {
                await ctx.Response.WriteAsync(html, token);
                await ctx.Response.WriteAsync(bootstrap, token);
                continue;
            }
            if (authPage && part == "admin/_auth.html")
                html = html.Replace("<!--__BOOTSTRAP__-->", bootstrap, StringComparison.Ordinal);
            await ctx.Response.WriteAsync(html, token);
        }
    }

    // Everything the console needs for its first paint.
    private static async Task<string> BootstrapAsync(AppDbContext db, HttpContext ctx, bool authPage)
    {
        object payload;

        var user = await AdminAuth.ResolveAsync(db, ctx);

        if (user is null)
        {
            // The sign-in screen paints its provider buttons from this, it never waits on a round trip to find out whether there are any.
            payload = new { authenticated = false, providers = authPage ? await OidcEndpoints.OfferedAsync(db, console: true) : new List<OidcButton>() };
        }
        else if (authPage)
        {
            // Pending session: only the fact that the password must change.
            payload = new { authenticated = true, mustChangePassword = user.MustChangePassword, user = new { user.Username } };
        }
        else
        {
            var tables = await db.Tables.Include(t => t.Fields).ToListAsync();
            var formCounts = await db.FormConfigs.GroupBy(f => f.TableId)
                .Select(g => new { TableId = g.Key, Count = g.Count() }).ToListAsync();
            var recordCounts = await db.Records.GroupBy(r => r.TableId)
                .Select(g => new { TableId = g.Key, Count = g.Count() }).ToListAsync();
            var settings = await db.SettingsAsync() ?? new AppSettings();

            payload = new
            {
                authenticated = true,
                // Linked, not the subject itself: the console decides whether to offer a link, and never needs the identity behind one.
                user = new { user.Username, user.Email, user.Role, Linked = user.OidcSubject.Length > 0 },
                tables = tables.Select(t => ApiDtos.TableDto(
                    t,
                    formCounts.FirstOrDefault(f => f.TableId == t.Id)?.Count ?? 0,
                    recordCounts.FirstOrDefault(r => r.TableId == t.Id)?.Count ?? 0)),
                settings = new { settings.AppName, settings.Currency, settings.TimeZone },
                // The field type picker paints from this, the console can never offer a type the server does not know.
                fieldTypes = FieldTypes.All.Select(t => new { t.Name, t.Label, t.Group, t.Aliases, Shape = t.Shape.ToString().ToLowerInvariant(), t.Nestable }),
                fieldTypeGroups = FieldGroups.Order,
                stats = new
                {
                    dbSizeBytes = ApiDtos.DatabaseBytes(db),
                    estimatedIndexBytes = ApiDtos.EstimatedIndexBytes(tables,
                        id => recordCounts.FirstOrDefault(r => r.TableId == id)?.Count ?? 0),
                    usersEnabled = await db.UserAccounts.CountAsync(u => !u.IsDisabled)
                }
            };
        }

        return Html.BootstrapScript(payload);
    }
}
