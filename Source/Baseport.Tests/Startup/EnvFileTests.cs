using Microsoft.Extensions.Configuration;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class EnvFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "baseport-env-" + Ids.NewShortId(8));

    public EnvFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void Write(string text) => File.WriteAllText(EnvFile.Path(_directory), text);

    [Fact]
    public void ParsesNamesAliasesAndQuotes()
    {
        var parsed = EnvFile.Parse("""
            # comment
            Baseport__AdminAddress=127.0.0.1:5264
            export BASEPORT_TRUST_FORWARDED_HEADERS=true
            BASEPORT_CONNECTION_STRING="Data Source=/data/base port.db"
            BASEPORT_ALLOW_INSECURE_SIGNIN='a#b'
            BASEPORT_URLS=http://0.0.0.0:5000 # public
            BASEPORT_TAG=latest
            """);

        var values = parsed.Entries.ToDictionary(e => e.Key!, e => e.Value);
        Assert.Equal("127.0.0.1:5264", values["Baseport:AdminAddress"]);
        Assert.Equal("true", values["Baseport:TrustForwardedHeaders"]);
        Assert.Equal("Data Source=/data/base port.db", values["Baseport:ConnectionString"]);
        Assert.Equal("a#b", values["Baseport:AllowInsecureSignIn"]);
        Assert.Equal("http://0.0.0.0:5000", values["urls"]);
        Assert.Equal(5, values.Count);
        Assert.Empty(parsed.Problems);
    }

    [Theory]
    [InlineData("NOT A LINE", "not NAME=value")]
    [InlineData("=value", "not NAME=value")]
    [InlineData("SOMETHING=1", "unknown name SOMETHING")]
    [InlineData("Baseport__AdminAdress=x", "unknown setting Baseport:AdminAdress")]
    public void ReportsBadLines(string line, string problem)
    {
        var parsed = EnvFile.Parse(line);

        Assert.Contains(parsed.Problems, p => p.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsRepeatedNamesAndLastWins()
    {
        var parsed = EnvFile.Parse("BASEPORT_ADMIN_ADDRESS=a:1\nBaseport__AdminAddress=b:2\n");

        Assert.Contains(parsed.Problems, p => p.Contains("lines 1, 2; line 2 wins", StringComparison.Ordinal));
        Write("BASEPORT_ADMIN_ADDRESS=a:1\nBaseport__AdminAddress=b:2\n");
        var config = new ConfigurationBuilder().Add(EnvFile.Source(_directory)).Build();
        Assert.Equal("b:2", config["Baseport:AdminAddress"]);
    }

    [Fact]
    public void ValuesAreNeverExecuted()
    {
        Write("BASEPORT_ADMIN_ADDRESS=$(touch pwned)`id`\n");

        var config = new ConfigurationBuilder().Add(EnvFile.Source(_directory)).Build();

        Assert.Equal("$(touch pwned)`id`", config["Baseport:AdminAddress"]);
        Assert.False(File.Exists("pwned"));
    }

    [Fact]
    public void SitsBetweenJsonAndTheEnvironment()
    {
        File.WriteAllText(Path.Combine(_directory, "appsettings.json"), """{"Baseport":{"AdminAddress":"json:1","AllowRemoteProviders":"false"}}""");
        Write("BASEPORT_ADMIN_ADDRESS=env-file:2\nBASEPORT_ALLOW_REMOTE_PROVIDERS=true\n");

        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(_directory, "appsettings.json"))
            .Add(EnvFile.Source(_directory))
            .AddInMemoryCollection([new("Baseport:AllowRemoteProviders", "environment")])
            .Build();

        Assert.Equal("env-file:2", config["Baseport:AdminAddress"]);
        Assert.Equal("environment", config["Baseport:AllowRemoteProviders"]);
    }

    [Fact]
    public void InsertsBeforeEnvironment()
    {
        var sources = new List<IConfigurationSource>
        {
            new Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource { Prefix = "ASPNETCORE_" },
            new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource { Path = "appsettings.json" },
            new Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource(),
            new Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationSource()
        };
        var envFile = EnvFile.Source(_directory);

        EnvFile.InsertBeforeEnvironment(sources, envFile);

        Assert.Same(envFile, sources[2]);
        Assert.IsType<Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource>(sources[3]);
    }

    [Theory]
    [InlineData("/opt/baseport/Baseport", "/var/tmp/.net/x/", "/opt/baseport")]
    [InlineData("/usr/share/dotnet/dotnet", "/src/bin/Debug/", "/src/bin/Debug/")]
    [InlineData(null, "/src/bin/Debug/", "/src/bin/Debug/")]
    public void EnvFileSitsBesideTheExecutable(string? processPath, string baseDirectory, string expected)
    {
        Assert.Equal(expected, EnvFile.InstallDirectory(processPath, baseDirectory));
    }

    [Fact]
    public void ComposeAndDockerUseAliasesOnly()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "docker-compose.yml"))) root = Path.GetDirectoryName(root)!;
        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var example = EnvFile.Parse(File.ReadAllText(Path.Combine(root, ".env.example")));

        Assert.DoesNotContain("Baseport__", compose);
        Assert.DoesNotContain("Baseport__", dockerfile);
        Assert.Contains("path: .env", compose);
        Assert.Empty(example.Problems);
    }

    [Fact]
    public void EnvironmentAliasesMapToKeys()
    {
        Environment.SetEnvironmentVariable("BASEPORT_ADMIN_ADDRESS", "127.0.0.1:7777");
        try
        {
            var config = new ConfigurationBuilder().Add(EnvFile.EnvironmentAliases()).Build();
            Assert.Equal("127.0.0.1:7777", config["Baseport:AdminAddress"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BASEPORT_ADMIN_ADDRESS", null);
        }
    }

    [Fact]
    public void CollisionsAreReported()
    {
        var layers = new List<ConfigCli.Layer>
        {
            new("appsettings.json", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Baseport:AdminAddress"] = "json:1" }),
            new(EnvFile.FileName, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Baseport:AdminAddress"] = "env:2", ["Baseport:TrustForwardedHeaders"] = "true" }),
            new("environment", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Baseport:TrustForwardedHeaders"] = "false" })
        };

        var report = ConfigCli.Inspect(layers, []);

        Assert.Contains("Baseport:AdminAddress is set in .env and appsettings.json; .env wins", report.Problems);
        Assert.Contains("Baseport:TrustForwardedHeaders is set in .env and the environment; the environment wins", report.Problems);
        Assert.Contains(report.Settings, s => s.Key == "Baseport:AdminAddress" && s.Value == "env:2" && s.Source == ".env");
        Assert.Contains(report.Settings, s => s.Key == "Baseport:TrustForwardedHeaders" && s.Source == "environment");
    }

    [Fact]
    public void NoCollisionNoProblem()
    {
        var layers = new List<ConfigCli.Layer>
        {
            new("appsettings.json", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Baseport:ConnectionString"] = "Data Source=x.db" }),
            new(EnvFile.FileName, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Baseport:AdminAddress"] = "env:2" })
        };

        Assert.Empty(ConfigCli.Inspect(layers, []).Problems);
    }
}
