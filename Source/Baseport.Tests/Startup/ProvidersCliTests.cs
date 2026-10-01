using Xunit;
using Baseport;
using Microsoft.EntityFrameworkCore;

namespace Baseport.Tests;

[Collection("Cli env var")]
public class ProvidersCliTests : IDisposable
{
    private readonly string _directory;
    private readonly string _connectionString;

    public ProvidersCliTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "baseport-cli-" + Ids.NewShortId(8));
        Directory.CreateDirectory(_directory);
        _connectionString = $"Data Source={Path.Combine(_directory, "cli.db")}";
        Environment.SetEnvironmentVariable("Baseport__ConnectionString", _connectionString);
    }

    private AppDbContext Open() =>
        TestDb.Open(_connectionString);

    private static Task<int> RunAsync(params string[] args) =>
        ProvidersCli.RunAsync(["providers", .. args], "missing.json", "missing.json");

    private async Task EnsureMigratedAsync()
    {
        using var db = Open();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    private async Task<AppSettings> SettingsAsync()
    {
        using var db = Open();
        return await db.SettingsAsync() ?? new AppSettings();
    }

    [Fact]
    public async Task EnablePostgresDefaults()
    {
        await EnsureMigratedAsync();

        Assert.Equal(0, await RunAsync("postgres", "enable"));

        var settings = await SettingsAsync();
        Assert.True(settings.PostgresEnabled);
    }

    [Fact]
    public async Task EnablePostgresWithOptions()
    {
        await EnsureMigratedAsync();

        Assert.Equal(0, await RunAsync("postgres", "enable", "--port", "6543", "--bind", "127.0.0.1"));

        var settings = await SettingsAsync();
        Assert.True(settings.PostgresEnabled);
        Assert.Equal(6543, settings.PostgresPort);
        Assert.Equal("127.0.0.1", settings.PostgresBindAddress);
    }

    [Fact]
    public async Task DisableTds()
    {
        await EnsureMigratedAsync();
        await RunAsync("tds", "enable");

        Assert.Equal(0, await RunAsync("tds", "disable"));

        var settings = await SettingsAsync();
        Assert.False(settings.TdsEnabled);
    }

    [Fact]
    public async Task PortRangeEnforced()
    {
        await EnsureMigratedAsync();

        Assert.Equal(1, await RunAsync("postgres", "enable", "--port", "70000"));

        var settings = await SettingsAsync();
        Assert.False(settings.PostgresEnabled);
    }

    [Fact]
    public async Task InvalidBindRefused()
    {
        await EnsureMigratedAsync();

        Assert.Equal(1, await RunAsync("postgres", "enable", "--bind", "not-an-ip"));

        var settings = await SettingsAsync();
        Assert.False(settings.PostgresEnabled);
    }

    [Fact]
    public async Task StatusIsReadOnly()
    {
        await EnsureMigratedAsync();
        await RunAsync("postgres", "enable", "--port", "5433");

        Assert.Equal(0, await RunAsync("status"));

        var settings = await SettingsAsync();
        Assert.True(settings.PostgresEnabled);
        Assert.Equal(5433, settings.PostgresPort);
    }

    [Fact]
    public async Task UnknownCommandFails()
    {
        await EnsureMigratedAsync();

        Assert.Equal(1, await RunAsync("frobnicate"));
    }

    [Fact]
    public async Task NoCommandShowsUsage()
    {
        await EnsureMigratedAsync();

        Assert.Equal(0, await RunAsync());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Baseport__ConnectionString", null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
