using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Baseport;

// Serves the admin console. Two jobs, both about doing work here rather than in the browser: 1.
public static class ConsoleEndpoints
{
    // The console owns a prefix, not the site root, so hosted forms and embeds have somewhere to live and "is this admin?" is a path test.
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

    // The default encoder escapes <, > and & as \u003C and friends, which is what stops a value in the payload from closing the script element it sits in.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
    };

    public static void MapConsoleEndpoints(this WebApplication app)
    {
        var webRoot = app.Environment.WebRootPath;

        // Deep links render the same document; the client router reads the path.
        app.MapGet(Base, (AppDbContext db, HttpContext ctx) => RenderAsync(db, ctx, webRoot, authPage: false));
        app.MapGet($"{Base}/{{**rest}}", (AppDbContext db, HttpContext ctx) => RenderAsync(db, ctx, webRoot, authPage: false));
        app.MapGet(Auth, (AppDbContext db, HttpContext ctx) => RenderAsync(db, ctx, webRoot, authPage: true));

        // Root answers 404, like TrailBase: the console owns /_/admin and nothing else, so a scan of "/" reveals neither the console nor a login form.

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
        var userId = AdminAuth.UserIdFor(ctx);
        var user = userId is null ? null : await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId);
        var signedIn = user is not null && !user.IsDisabled;
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
        // The payload is per-session, so a shared cache must never serve one user's bootstrap to another.
        ctx.Response.Headers.CacheControl = "no-store";

        var token = ctx.RequestAborted;
        var parts = authPage ? AuthParts : Parts;
        foreach (var part in parts)
        {
            var html = await File.ReadAllTextAsync(Path.Combine(webRoot, part), token);
            // The bootstrap goes immediately before the scripts that read it.
            if (!authPage && part == "admin/_shell.html")
            {
                await ctx.Response.WriteAsync(html, token);
                await ctx.Response.WriteAsync(await BootstrapAsync(db, ctx, authPage), token);
                continue;
            }
            if (authPage && part == "admin/_auth.html")
                html = html.Replace("<!--__BOOTSTRAP__-->", await BootstrapAsync(db, ctx, authPage), StringComparison.Ordinal);
            await ctx.Response.WriteAsync(html, token);
        }
    }

    // Everything the console needs for its first paint.
    private static async Task<string> BootstrapAsync(AppDbContext db, HttpContext ctx, bool authPage)
    {
        object payload;

        var userId = AdminAuth.UserIdFor(ctx);
        var user = userId is null ? null : await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId);

        if (user is null || user.IsDisabled)
        {
            payload = new { authenticated = false };
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
                user = new { user.Username, user.Email, user.Role },
                tables = tables.Select(t => ApiDtos.TableDto(
                    t,
                    formCounts.FirstOrDefault(f => f.TableId == t.Id)?.Count ?? 0,
                    recordCounts.FirstOrDefault(r => r.TableId == t.Id)?.Count ?? 0)),
                settings = new { settings.AppName, settings.Currency }
            };
        }

        // A JSON script block, not a JS literal: the browser parses it as data, so nothing in it executes even if it reaches the DOM.
        var json = JsonSerializer.Serialize(payload, Json);

        // Belt and braces.
        if (json.Contains('<')) json = json.Replace("<", "\\u003C", StringComparison.Ordinal);

        return $"\n<script type=\"application/json\" id=\"bootstrap\">{json}</script>\n";
    }
}
