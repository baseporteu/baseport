using Microsoft.EntityFrameworkCore;

namespace Baseport;

public static class ConsoleEndpoints
{

    private const string Base = "/_/admin";

    private const string Auth = "/_/auth";

    private static readonly string[] Parts =
    {
        "admin/_shell.html",
        "admin/views/tables.html",
        "admin/views/forms.html",
        "admin/views/actions.html",
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

        app.MapGet(Base, (AppDbContext db, HttpContext ctx) => RenderAsync(db, ctx, webRoot, authPage: false));
        app.MapGet($"{Base}/{{**rest}}", (AppDbContext db, HttpContext ctx) => RenderAsync(db, ctx, webRoot, authPage: false));
        app.MapGet(Auth, (AppDbContext db, HttpContext ctx) => RenderAsync(db, ctx, webRoot, authPage: true));

        app.MapGet("/docs", () => Results.File(Path.Combine(webRoot, "docs.html"), "text/html; charset=utf-8"));

        app.MapFallback(async (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
                await ctx.Response.WriteAsJsonAsync(new { errors = new[] { "No such endpoint." } });
        });
    }

    private static async Task RenderAsync(AppDbContext db, HttpContext ctx, string webRoot, bool authPage)
    {

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

        ctx.Response.Headers.CacheControl = "no-store";

        var token = ctx.RequestAborted;

        var bootstrap = await BootstrapAsync(db, ctx, authPage);

        var parts = authPage ? AuthParts : Parts;
        foreach (var part in parts)
        {
            var html = await File.ReadAllTextAsync(Path.Combine(webRoot, part), token);

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

    private static async Task<string> BootstrapAsync(AppDbContext db, HttpContext ctx, bool authPage)
    {
        object payload;

        var user = await AdminAuth.ResolveAsync(db, ctx);

        if (user is null)
        {

            payload = new { authenticated = false, providers = authPage ? await OidcEndpoints.OfferedAsync(db, console: true) : new List<OidcButton>() };
        }
        else if (authPage)
        {

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

            var tableById = tables.ToDictionary(t => t.Id);
            var forms = await db.FormConfigs.OrderByDescending(f => f.Id).ToListAsync();
            var actions = await db.Actions.ToListAsync();
            var savedQueries = await db.SavedQueries.OrderBy(q => q.Name).ToListAsync();

            payload = new
            {
                authenticated = true,

                user = new { user.Username, user.Email, user.Role, Linked = user.OidcSubject.Length > 0, Avatar = Avatars.DataUri(user.Username) },
                tables = tables.Select(t => ApiDtos.TableDto(
                    t,
                    formCounts.FirstOrDefault(f => f.TableId == t.Id)?.Count ?? 0,
                    recordCounts.FirstOrDefault(r => r.TableId == t.Id)?.Count ?? 0)),
                forms = forms.Select(f => ApiDtos.FormDto(f, tableById.GetValueOrDefault(f.TableId))),
                actions = actions.Select(ApiDtos.ActionDto),
                sql = savedQueries.Select(AdminEndpoints.QueryDto),
                settings = new { settings.AppName, settings.Currency, settings.TimeZone },

                fieldTypes = FieldTypes.All.Select(t => new { t.Name, t.Label, t.Group, t.Aliases, Shape = t.Shape.ToString().ToLowerInvariant(), t.Nestable, t.Computed }),
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
