using Xunit;
using Baseport;

namespace Baseport.Tests;

// The instance time zone is handed to clients to render with, what has to hold is that an IANA-speaking client would accept it.
public class TimeZonesTests
{
    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Amsterdam")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("Etc/GMT+5")]
    public void Real_zones_are_accepted(string zone) => Assert.True(TimeZones.IsValid(zone));

    [Theory]
    [InlineData("")]
    [InlineData("Europe")]
    [InlineData("Europe/Amsterdam; DROP TABLE _settings")]
    [InlineData("../../etc/passwd")]
    [InlineData("Europe/Amsterdam/Extra/Deep")]
    public void Anything_that_is_not_a_zone_name_is_refused(string zone) => Assert.False(TimeZones.IsValid(zone));

    [Fact]
    public void The_host_zone_is_one_a_client_could_render_with()
    {
        // TimeZoneInfo.Local is a Windows name on Windows, which no browser accepts, the default has to survive being handed straight to Intl.
        Assert.True(TimeZones.IsValid(TimeZones.HostDefault));
        Assert.True(TimeZones.HostDefault == "UTC" || TimeZones.HostDefault.Contains('/'));
        Assert.Equal(TimeZones.HostDefault, new AppSettings().TimeZone);
    }

    [Fact]
    public void A_zone_name_cannot_be_arbitrarily_long() =>
        Assert.False(TimeZones.IsValid("Europe/" + new string('a', 100)));
}
