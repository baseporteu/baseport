using Microsoft.Extensions.Configuration;
using Xunit;
using Baseport;

namespace Baseport.Tests;

public class BundledSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "baseport-settings-" + Ids.NewShortId(8));

    public BundledSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ReadsTheFileBesideTheBinary()
    {
        File.WriteAllText(Path.Combine(_directory, BundledSettings.FileName), """{"Baseport":{"AdminAddress":"127.0.0.1:5264"}}""");

        var config = new ConfigurationBuilder().Add(BundledSettings.Source(_directory)).Build();

        Assert.Equal("127.0.0.1:5264", config["Baseport:AdminAddress"]);
    }

    [Fact]
    public void LaterSourcesOverrideIt()
    {
        File.WriteAllText(Path.Combine(_directory, BundledSettings.FileName), """{"Baseport":{"AdminAddress":"127.0.0.1:5264"}}""");

        var config = new ConfigurationBuilder()
            .Add(BundledSettings.Source(_directory))
            .AddInMemoryCollection([new("Baseport:AdminAddress", "127.0.0.1:6000")])
            .Build();

        Assert.Equal("127.0.0.1:6000", config["Baseport:AdminAddress"]);
    }

    [Fact]
    public void MissingFileIsNotAnError()
    {
        var config = new ConfigurationBuilder().Add(BundledSettings.Source(_directory)).Build();

        Assert.Null(config["Baseport:AdminAddress"]);
    }
}
