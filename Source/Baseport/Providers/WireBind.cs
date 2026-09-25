using System.Net;

namespace Baseport.Providers;

public static class WireBind
{
    public static bool RemoteAllowed { get; set; }

    public static string? Problem(string address, string protocol) => Problem(address, protocol, RemoteAllowed);

    public static string? Problem(string address, string protocol, bool remoteAllowed) =>
        !IPAddress.TryParse(address, out var ip) ? $"{protocol} bind address must be a valid IP address."
        : IPAddress.IsLoopback(ip) || remoteAllowed ? null
        : $"Binding the {protocol} listener beyond this machine needs Baseport:WireRemoteAccess in appsettings.json. " +
          "The protocol sends tokens in cleartext; put a TLS tunnel in front.";
}
