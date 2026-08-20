using System.Net;
using System.Net.Sockets;

namespace Baseport;

// Every outbound call Baseport makes on somebody's behalf goes through here first. A proxy target is a URL an operator types into the console, and the server fetches it from inside the network the server sits in: without a check that reaches cloud metadata (169.254.169.254), a neighbour's admin port, or anything else the operator's browser could not have reached itself.
//
// Private and loopback targets are refused by default and opened by one setting, because the intended target often is local (a Portway on the same host, an internal API on the LAN). Turning it on is the operator saying the server's own network is in scope, metadata endpoint included.
public static class ProxyTarget
{
    private static volatile bool _allowPrivate;

    public static void Configure(AppSettings settings) => _allowPrivate = settings.ProxyPrivateTargetsEnabled;

    // Returns null when the URL may be fetched, or the message to hand back when it may not.
    public static string? Problem(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "The URL must be an absolute http:// or https:// address.";

        if (_allowPrivate) return null;

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.IdnHost, out var literal)) addresses = [literal];
        else
        {
            // A name resolved here can resolve to something else when the request is made, so this narrows the surface rather than sealing it; the setting behind it is the real decision.
            try { addresses = Dns.GetHostAddresses(uri.IdnHost); }
            catch (SocketException) { return "The host could not be resolved."; }
            catch (ArgumentException) { return "The host could not be resolved."; }
        }

        return addresses.Any(IsPrivate)
            ? "That address is on a private or loopback network. Turn on \"Allow private proxy targets\" in Settings to reach it."
            : null;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || b[0] == 127
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] >= 224;
        }

        var v6 = address.GetAddressBytes();
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (v6[0] & 0xFE) == 0xFC;
    }
}
