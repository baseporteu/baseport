using Baseport;
using Baseport.Providers.Postgres;
using Baseport.Providers.Tds;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Serilog;

var bundledSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var localSettings = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
if (!File.Exists(bundledSettings) && !File.Exists(localSettings))
{
    Console.Error.WriteLine($"Baseport could not find appsettings.json in {AppContext.BaseDirectory} or {Directory.GetCurrentDirectory()}.");
    return 1;
}

if (args.Length > 0 && args[0] == "providers")
    return await ProvidersCli.RunAsync(args, bundledSettings, localSettings);

if (args.Length > 0 && args[0] == "accounts")
    return await AccountsCli.RunAsync(args, bundledSettings, localSettings);

if (args.Length > 0 && args[0] is "help" or "-h" or "--help")
    return CliHelp.List("commands", CliHelp.Commands);

if (args.Length > 0 && args[0] == "version")
{
    Console.WriteLine(CliHelp.Version);
    return 0;
}

if (args.Length > 0 && CliHelp.WrapperCommands.Contains(args[0]))
{
    Console.Error.WriteLine($"'{args[0]}' comes from the baseport wrapper script, not the binary. Run: baseport {args[0]}");
    return 1;
}

if (args.Length > 0 && !args[0].StartsWith('-')) return CliHelp.Invalid();

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

    var bundledWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        WebRootPath = Directory.Exists(bundledWebRoot) ? bundledWebRoot : null
    });
    builder.Host.UseSerilog();
    builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

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
    Baseport.Providers.WireBind.RemoteAllowed = config.GetValue("WireRemoteAccess", false);
    AdminAuth.AllowInsecureSignIn = config.GetValue("AllowInsecureSignIn", false);
    FileStore.Initialize(connectionString);

    var dbSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
    var dbFile = Path.GetFullPath(dbSource == ":memory:" ? "baseport.db" : dbSource);
    builder.Services.AddDataProtection()
        .SetApplicationName("Baseport")
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetDirectoryName(dbFile)!, "keys")));

    if (AdminSurface.Configure(config["AdminAddress"]) is { } adminUrl)
    {
        var configured = builder.Configuration["urls"] ?? "http://localhost:5000";
        builder.WebHost.UseUrls([.. configured.Split(';', StringSplitOptions.RemoveEmptyEntries), adminUrl]);
    }

    builder.Services.AddDbContextPool<AppDbContext>(options =>
        AppDbContext.Configure(options, connectionString));

    builder.Services.AddCors(options =>
        options.AddPolicy("embed", p => p
            .SetIsOriginAllowed(origin => AllowedOrigins.Allows(EmbedOrigins.Current, origin))
            .AllowAnyMethod()
            .AllowAnyHeader()));

    builder.Services.AddOutboundHttp();

    builder.Services.AddResponseCompression();

    builder.Services.AddBaseportRateLimiter();

    builder.Services.AddHostedService<JobScheduler>();

    builder.Services.AddSingleton<AuditLogWriter>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditLogWriter>());

    builder.Services.AddHostedService<PostgresServer>();
    builder.Services.AddHostedService<TdsServer>();

    var app = builder.Build();
    Ids.StartedAt = DateTime.UtcNow;

    if (trustForwardedHeaders)
    {
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });
    }

    if (!app.Environment.IsDevelopment()) app.UseHsts();

    app.UseHttpsRedirection();

    app.UseSerilogRequestLogging(options => options.GetLevel = (ctx, _, ex) =>
        ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
        : ctx.Response.StatusCode >= 400 ? Serilog.Events.LogEventLevel.Warning
        : IsStaticAsset(ctx.Request.Path) ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Debug);

    app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
    {
        var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;

        var status = ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
        if (ex != null && status >= 500) Log.Error(ex, "Unhandled exception on {Path}", ctx.Request.Path);

        ctx.Response.StatusCode = status;
        if (ctx.Request.Path.StartsWithSegments("/api"))
            await ctx.Response.WriteAsJsonAsync(new { errors = new[] { status >= 500 ? "Internal server error." : "The request could not be parsed." } });
    }));

    app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/api/forms"), b => b.UseCors("embed"));
    app.UseRateLimiter();
    app.UseSecurityHeaders();
    app.UseResponseCompression();

    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
    });

    Directory.CreateDirectory(FileStore.Directory);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(FileStore.Directory),
        RequestPath = "/uploads",
        ServeUnknownFileTypes = false
    });
    app.UseSameOriginWrites();
    app.UseAdminSurface();
    app.UseAuditLog();
    app.UseAdminAuth();

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Secrets.Configure(scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
        await SchemaBootstrap.ApplyAsync(db);
        await AdminAuth.EnsureAdminPasswordAsync(db);

        var settings = await db.SettingsAsync() ?? new AppSettings();
        PreviewAuth.Initialize(previewSecret ?? settings.PreviewSecret, TimeSpan.FromDays(1));

        await ActionDefCache.ReloadFromDbAsync(db);
    }

    app.MapAuthEndpoints();
    app.MapUserAuthEndpoints();
    app.MapOidcEndpoints();
    app.MapClientErrorEndpoints();
    app.MapStorageEndpoints();
    app.MapTableEndpoints();
    app.MapFormEndpoints();
    app.MapActionEndpoints();
    app.MapAdminEndpoints();
    app.MapPublicApiEndpoints();
    app.MapTransactionEndpoints();
    app.MapFragmentEndpoints();
    app.MapConsoleEndpoints();

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        foreach (var console in AdminSurface.ConsoleUrls(app.Urls, AdminSurface.Port))
            Log.Information("Console {Url}", console);

        if (AdminAuth.AllowInsecureSignIn)
            Log.Warning("Baseport:AllowInsecureSignIn is on. Sign-in works over plain HTTP and session cookies are not Secure; " +
                "anyone on the network path can take over a session. Turn it off before exposing this instance.");

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
