using System.Net;
using System.Net.NetworkInformation;

namespace Vendomat.Controller.Tablet.Services;

// Resolves the tablet's actual LAN IPv4 address and rewrites local API base URLs so paired
// clients connect by IP instead of relying on mDNS hostname resolution (e.g. "vendomat.local"),
// which is unreliable on many networks (Android hotspots, enterprise/guest Wi-Fi).
internal static class LanAddressResolver
{
    // Replaces a hostname (e.g. "vendomat.local") with the tablet's LAN IPv4 address while
    // preserving scheme and port. IP literals and unparseable values are returned unchanged.
    public static string ResolveBaseUrl(string? baseUrl)
    {
        if (!TryCreateBaseUri(baseUrl, out var baseUri))
        {
            return baseUrl?.Trim() ?? string.Empty;
        }

        var builder = new UriBuilder(baseUri)
        {
            Host = ResolveHost(baseUri.Host),
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }

    // Keeps an explicit IP literal as-is; otherwise substitutes the resolved LAN IPv4,
    // falling back to the original host when no suitable address can be found.
    public static string ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }

        return ResolveLanIpv4()?.ToString() ?? host;
    }

    public static bool TryCreateBaseUri(string? value, out Uri baseUri)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            baseUri = default!;
            return false;
        }

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"http://{normalized}";
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out baseUri);
    }

    public static IEnumerable<IPAddress> GetLocalIpAddresses()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            IPInterfaceProperties? properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (var unicastAddress in properties.UnicastAddresses)
            {
                yield return unicastAddress.Address;
            }
        }
    }

    private static IPAddress? ResolveLanIpv4()
    {
        IPAddress? fallback = null;
        foreach (var address in GetLocalIpAddresses())
        {
            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || IPAddress.IsLoopback(address))
            {
                continue;
            }

            // Prefer RFC 1918 private addresses; keep any other routable IPv4 as a fallback.
            if (IsPrivateIPv4(address))
            {
                return address;
            }

            fallback ??= address;
        }

        return fallback;
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}
