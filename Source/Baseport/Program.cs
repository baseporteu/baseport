using Baseport;
using Baseport.Providers.Postgres;
using Baseport.Providers.Tds;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Serilog;

// Defaults ship inside the executable and extract beside it; an operator's own copy sits in the directory they run from and wins.
var bundledSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var localSettings = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
if (!File.Exists(bundledSettings) && !File.Exists(localSettings))
{
    Console.Error.WriteLine($"Baseport could not find appsettings.json in {AppContext.BaseDirectory} or {Directory.GetCurrentDirectory()}.");
    return 1;
}

// A short-lived CLI mode that edits the same settings row the admin UI does, instead of standing up the web server.
if (args.Length > 0 && args[0] == "providers")
    return await ProvidersCli.RunAsync(args, bundledSettings, localSettings);

// The operations the console refuses on an admin account live here instead, where they need shell access.
if (args.Length > 0 && args[0] == "accounts")
    return await AccountsCli.RunAsync(args, bundledSettings, localSettings);

// Logging is not up yet, so a failure here can only report itself.
var logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "log");
try
{
    if (!Directory.Exists(logDirectory)) Directory.CreateDirectory(logDirectory);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Baseport could not create its log directory at {logDirectory}: {ex.Message}");
    return 1;
}

try
{
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(new ConfigurationBuilder()
            .AddJsonFile(bundledSettings, optional: true, reloadOnChange: true)
            .AddJsonFile(localSettings, optional: true, reloadOnChange: true)
            .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), $"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json"), optional: true)
            .Build())
        .CreateLogger();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Baseport could not read appsettings.json: {ex.Message}");
    return 1;
}

try
{
    Log.Information("");
    Log.Information("██████╗  █████╗ ███████╗███████╗██████╗  ██████╗ ██████╗ ████████╗");
    Log.Information("██╔══██╗██╔══██╗██╔════╝██╔════╝██╔══██╗██╔═══██╗██╔══██╗╚══██╔══╝");
    Log.Information("██████╔╝███████║███████╗█████╗  ██████╔╝██║   ██║██████╔╝   ██║   ");
    Log.Information("██╔══██╗██╔══██║╚════██║██╔══╝  ██╔═══╝ ██║   ██║██╔══██╗   ██║   ");
    Log.Information("██████╔╝██║  ██║███████║███████╗██║     ╚██████╔╝██║  ██║   ██║   ");
    Log.Information("╚═════╝ ╚═╝  ╚═╝╚══════╝╚══════╝╚═╝      ╚═════╝ ╚═╝  ╚═╝   ╚═╝   ");
    Log.Information("");

    // Published as a single file, wwwroot travels inside the executable and is extracted beside the host, not into the directory the operator runs from, which is where the content root points.
    var bundledWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        WebRootPath = Directory.Exists(bundledWebRoot) ? bundledWebRoot : null
    });
    builder.Host.UseSerilog();
    builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

    // CreateBuilder only looks in the content root, which a single-file publish leaves empty.
    builder.Configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
    {
        Path = bundledSettings,
        Optional = true,
        ReloadOnChange = true,
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory)
    });

    var config = builder.Configuration.GetSection("Baseport");
    var connectionString = config["ConnectionString"] ?? "Data Source=baseport.db";
    var previewSecret = config["PreviewSecret"];
    var trustForwardedHeaders = config.GetValue("TrustForwardedHeaders", false);
    FileStore.Initialize(connectionString);

    // A second listener for the console, so it can be bound to a private interface while the public API stays reachable.
    if (AdminSurface.Configure(config["AdminAddress"]) is { } adminUrl)
    {
        var configured = builder.Configuration["urls"] ?? "http://localhost:5000";
        builder.WebHost.UseUrls([.. configured.Split(';', StringSplitOptions.RemoveEmptyEntries), adminUrl]);
    }

    // Pooled: a DbContext is a request-lifetime allocation with a change tracker behind it, and this one is created for every request including the ones that only read.
    builder.Services.AddDbContextPool<AppDbContext>(options =>
        options.UseSqlite(connectionString).AddInterceptors(new SqlitePragmas(), new RecordChangeInterceptor()));

    // The embed runs on customer domains, so the form routes must be callable cross-origin.
    builder.Services.AddCors(options =>
        options.AddPolicy("embed", p => p
            .SetIsOriginAllowed(origin => AllowedOrigins.Allows(EmbedOrigins.Current, origin))
            .AllowAnyMethod()
            .AllowAnyHeader()));

    // Outgoing HTTP for the OpenAPI proxy import and proxy-table forwarding.
    builder.Services.AddHttpClient();

    // The vendored Scalar bundle is 3.6 MB of JavaScript.
    builder.Services.AddResponseCompression();

    builder.Services.AddBaseportRateLimiter();

    // Runs due maintenance jobs (backups, cleanup) on their cron schedules.
    builder.Services.AddHostedService<JobScheduler>();

    // One instance, reachable both as the queue the middleware writes to and as the background service that drains it.
    builder.Services.AddSingleton<AuditLogWriter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditLogWriter>());

    // optional wire-protocol listeners: always registered, but each polls its own AppSettings row and only opens its port once an operator enables it from the admin ui or the cli — a second authentication surface (password field = api token) alongside the rest api
    builder.Services.AddHostedService<PostgresServer>();
    builder.Services.AddHostedService<TdsServer>();

    var app = builder.Build();
    Ids.StartedAt = DateTime.UtcNow;

    // Rate limiting keys on the client address, so behind a reverse proxy the forwarded headers must be honoured or every visitor shares one bucket.
    if (trustForwardedHeaders)
    {
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });
    }

    // HSTS only outside Development: it pins the browser to HTTPS for the max-age window, which turns a self-signed dev cert into a lockout.
    if (!app.Environment.IsDevelopment()) app.UseHsts();
    // A no-op warning, not a failure, when no HTTPS endpoint is configured (the default local http-only run) or the proxy hasn't set TrustForwardedHeaders.
    app.UseHttpsRedirection();

    // One line per request buries the one line that matters, and retainedFileCountLimit caps files, not bytes: at 1000 req/s that is ~7.6 GB a day.
    app.UseSerilogRequestLogging(options => options.GetLevel = (ctx, _, ex) =>
        ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
        : ctx.Response.StatusCode >= 400 ? Serilog.Events.LogEventLevel.Warning
        : IsStaticAsset(ctx.Request.Path) ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Debug);

    app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
    {
        var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        // A request ASP.NET could not parse is the caller's error and already carries the status that says so; flattening it to 500 reported a server fault for something like ?pageSize=2147483648, and logged it as one.
        var status = ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
        if (ex != null && status >= 500) Log.Error(ex, "Unhandled exception on {Path}", ctx.Request.Path);
        // The OpenAPI document promises every non-2xx answer speaks the Error shape, so an unhandled exception must too, instead of ASP.NET's bare 500.
        ctx.Response.StatusCode = status;
        if (ctx.Request.Path.StartsWithSegments("/api"))
            await ctx.Response.WriteAsJsonAsync(new { errors = new[] { status >= 500 ? "Internal server error." : "The request could not be parsed." } });
    }));

    // Only the visitor-facing form routes are reachable cross-origin, and only from the sites an author listed.
    app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api/forms"), b => b.UseCors("embed"));
    app.UseRateLimiter();
    app.UseSecurityHeaders();
    app.UseResponseCompression();

    // The console's assets carry no version in their URL, so a browser holding a stale ui.js after an upgrade runs it against fresh markup and the page breaks with something like "ui.themeChoice is not a function". no-cache still revalidates cheaply: the ETag answers 304 and nothing is downloaded twice.
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
    });
    
    // Uploaded files: a file field stores an absolute URL, so it must be fetchable the same way any other URL in that field would be -- no session, no token.
    Directory.CreateDirectory(FileStore.Directory);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(FileStore.Directory),
        RequestPath = "/uploads",
        ServeUnknownFileTypes = false
    });
    app.UseAdminSurface();
    app.UseAuditLog();
    app.UseAdminAuth();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SchemaBootstrap.ApplyAsync(db);
        await AdminAuth.EnsureAdminPasswordAsync(db);

        // Configuration wins when an operator sets one; otherwise the per-instance secret the bootstrap generated.
        var settings = await db.SettingsAsync() ?? new AppSettings();
        PreviewAuth.Initialize(previewSecret ?? settings.PreviewSecret, TimeSpan.FromDays(1));
    }

    app.MapAuthEndpoints();
    app.MapUserAuthEndpoints();
    app.MapOidcEndpoints();
    app.MapClientErrorEndpoints();
    app.MapStorageEndpoints();
    app.MapTableEndpoints();
    app.MapFormEndpoints();
    app.MapAdminEndpoints();
    app.MapPublicApiEndpoints();

    // The console is composed and its first payload rendered server-side.
    app.MapFragmentEndpoints();
    app.MapConsoleEndpoints();

    // Logged from the started event, not before Run: announcing the console and then failing to bind is worse than saying nothing.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Urls.Count > 0 ? string.Join(", ", app.Urls) : "the configured address";
        Log.Information("Baseport listening on {Addresses}/_/admin", addresses);

        // the session cookie is only Secure over HTTPS, so plain HTTP on a reachable (non-loopback) address ships it in the clear
        foreach (var url in app.Urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "http" || u.IsLoopback) continue;
            Log.Warning("Listening on {Url} without TLS. The session cookie is sent in the clear on this address; " +
                "put a TLS-terminating proxy in front (with Baseport:TrustForwardedHeaders set), or bind to loopback only.", url);
        }
    });

    app.Run();
    return 0;
}
catch (Exception ex)
{
    // A recognised failure is a configuration mistake: one line, no stack trace.
    var known = StartupFailure.Describe(ex);
    if (known is not null)
    {
        Log.Fatal("Baseport could not start. {Reason}", known);
        Log.Debug(ex, "Startup failure detail");
        return 1;
    }

    Log.Fatal(ex, "Baseport terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

// Assets the browser re-requests on every page load, mostly answered 304.
static bool IsStaticAsset(PathString path)
{
    var value = path.Value;
    if (string.IsNullOrEmpty(value)) return false;
    return value.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
        || value.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase);
}

public partial class Program;
