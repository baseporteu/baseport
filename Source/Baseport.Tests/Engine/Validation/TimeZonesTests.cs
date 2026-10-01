using Xunit;
using Baseport;

namespace Baseport.Tests;

public class TimeZonesTests
{
    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Amsterdam")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("Etc/GMT+5")]
    public void RealZonesAccepted(string zone) => Assert.True(TimeZones.IsValid(zone));

    [Theory]
    [InlineData("")]
    [InlineData("Europe")]
    [InlineData("Europe/Amsterdam; DROP TABLE _settings")]
    [InlineData("../../etc/passwd")]
    [InlineData("Europe/Amsterdam/Extra/Deep")]
    public void NonZoneRefused(string zone) => Assert.False(TimeZones.IsValid(zone));

    [Fact]
    public void HostZoneIsRenderable()
    {

        Assert.True(TimeZones.IsValid(TimeZones.HostDefault));
        Assert.True(TimeZones.HostDefault == "UTC" || TimeZones.HostDefault.Contains('/'));
        Assert.Equal(TimeZones.HostDefault, new AppSettings().TimeZone);
    }

    [Fact]
    public void ZoneNameLengthCapped() =>
        Assert.False(TimeZones.IsValid("Europe/" + new string('a', 100)));
}
