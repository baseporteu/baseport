namespace Baseport;

public static class TimeZones
{
    private const int MaxLength = 64;

    public static string HostDefault { get; } = Host();

    private static string Host()
    {
        var id = TimeZoneInfo.Local.Id;
        return id == "UTC" || (id.Contains('/') && IsValid(id)) ? id : "UTC";
    }

    public static bool IsValid(string zone)
    {
        if (string.IsNullOrEmpty(zone) || zone.Length > MaxLength) return false;

        return TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _) || WellFormed(zone);
    }

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
