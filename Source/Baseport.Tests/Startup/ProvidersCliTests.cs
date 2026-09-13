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
    public async Task Enabling_postgres_persists_the_default_port_and_bind_address()
    {
        await EnsureMigratedAsync();

        Assert.Equal(0, await RunAsync("postgres", "enable"));

        var settings = await SettingsAsync();
        Assert.True(settings.PostgresEnabled);
    }

    [Fact]
    public async Task Enabling_postgres_with_port_and_bind_persists_both()
    {
        await EnsureMigratedAsync();

        Assert.Equal(0, await RunAsync("postgres", "enable", "--port", "6543", "--bind", "127.0.0.1"));

        var settings = await SettingsAsync();
        Assert.True(settings.PostgresEnabled);
        Assert.Equal(6543, settings.PostgresPort);
        Assert.Equal("127.0.0.1", settings.PostgresBindAddress);
    }

    [Fact]
    public async Task Disabling_tds_clears_the_enabled_flag()
    {
        await EnsureMigratedAsync();
        await RunAsync("tds", "enable");

        Assert.Equal(0, await RunAsync("tds", "disable"));

        var settings = await SettingsAsync();
        Assert.False(settings.TdsEnabled);
    }

    [Fact]
    public async Task An_out_of_range_port_is_refused()
    {
        await EnsureMigratedAsync();

        Assert.Equal(1, await RunAsync("postgres", "enable", "--port", "70000"));

        var settings = await SettingsAsync();
        Assert.False(settings.PostgresEnabled);
    }

    [Fact]
    public async Task An_invalid_bind_address_is_refused()
    {
        await EnsureMigratedAsync();

        Assert.Equal(1, await RunAsync("postgres", "enable", "--bind", "not-an-ip"));

        var settings = await SettingsAsync();
        Assert.False(settings.PostgresEnabled);
    }

    [Fact]
    public async Task Status_reports_the_current_configuration_without_changing_it()
    {
        await EnsureMigratedAsync();
        await RunAsync("postgres", "enable", "--port", "5433");

        Assert.Equal(0, await RunAsync("status"));

        var settings = await SettingsAsync();
        Assert.True(settings.PostgresEnabled);
        Assert.Equal(5433, settings.PostgresPort);
    }

    [Fact]
    public async Task An_unknown_command_reports_usage_and_fails()
    {
        await EnsureMigratedAsync();

        Assert.Equal(1, await RunAsync("frobnicate"));
    }

    [Fact]
    public async Task No_command_reports_usage_and_succeeds()
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
