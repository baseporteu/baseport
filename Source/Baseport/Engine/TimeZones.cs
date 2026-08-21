namespace Baseport;

// The instance time zone is a display default handed to clients, what has to hold is that an IANA-speaking client would accept it.
public static class TimeZones
{
    private const int MaxLength = 64;

    // What a fresh instance starts on: the host's own zone, which is the clock the operator reading the console is already keeping. Linux names it the way a browser does; a win-x64 build in invariant globalization mode says "W. Europe Standard Time", which no client can read, UTC stands there.
    public static string HostDefault { get; } = Host();

    private static string Host()
    {
        var id = TimeZoneInfo.Local.Id;
        return id == "UTC" || (id.Contains('/') && IsValid(id)) ? id : "UTC";
    }

    public static bool IsValid(string zone)
    {
        if (string.IsNullOrEmpty(zone) || zone.Length > MaxLength) return false;
        // Linux includes the tz database, this settles it there. A win-x64 build in invariant globalization mode maps no IANA name at all, and refusing every zone but UTC on Windows would be worse than accepting a well-formed one.
        return TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _) || WellFormed(zone);
    }

    // Area/Location, the shape every IANA name takes, or the bare UTC that needs no area.
    private static bool WellFormed(string zone)
    {
        if (zone is "UTC") return true;
        var parts = zone.Split('/');
        if (parts.Length is < 2 or > 3) return false;
        foreach (var part in parts)
        {
            if (part.Length == 0 || !char.IsAsciiLetter(part[0])) return false;
            foreach (var c in part)
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '+')) return false;
        }
        return true;
    }
}
