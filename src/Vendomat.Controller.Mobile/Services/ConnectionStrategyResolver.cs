using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Maui.Networking;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Mobile.Models;

namespace Vendomat.Controller.Mobile.Services;

public static class ConnectionStrategyResolver
{
    public static IReadOnlyList<ConnectionEndpointCandidate> GetCandidates(PairedMachineRecord record)
    {
        var localHint = !string.IsNullOrWhiteSpace(record.LocalSecureApiBaseUrl)
            ? record.LocalSecureApiBaseUrl
            : record.LocalApiBaseUrl;
        var localAvailable = IsSameLocalNetwork(localHint);
        var shouldTryLocalFirst = ShouldTryLocalFirst(record, localAvailable);
        var directFallback = BuildDirectCandidate(record.PublicApiBaseUrl, record);
        var cloudFallback = BuildCloudCandidate(record.CloudApiBaseUrl);
        var localFallback = BuildLocalCandidate(localHint);
        List<ConnectionEndpointCandidate?> candidates = record.PreferredConnectionPreference switch
        {
            MachineConnectionPreference.LocalFirst => [localFallback, directFallback, cloudFallback],
            MachineConnectionPreference.DirectFirst => [directFallback, localAvailable ? localFallback : null, cloudFallback, localAvailable ? null : localFallback],
            MachineConnectionPreference.CloudBridgeOnly => [cloudFallback],
            _ => shouldTryLocalFirst
                ? [localFallback, directFallback, cloudFallback]
                : [directFallback, cloudFallback, localFallback],
        };

        return Normalize(candidates, record);
    }

    public static IReadOnlyList<ConnectionEndpointCandidate> GetCandidates(PairingQrPayload payload)
    {
        var localHint = !string.IsNullOrWhiteSpace(payload.LocalSecureApiBaseUrl)
            ? payload.LocalSecureApiBaseUrl
            : payload.LocalApiBaseUrl;
        var candidates = new ConnectionEndpointCandidate?[]
        {
            BuildDirectCandidate(payload.PublicApiBaseUrl),
            BuildCloudCandidate(payload.CloudApiBaseUrl),
            BuildLocalCandidate(localHint),
        };

        return Normalize(candidates);
    }

    public static bool IsSameLocalNetwork(string? apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            return false;
        }

        var profiles = Connectivity.Current.ConnectionProfiles;
        var hasLocalNetwork = profiles.Contains(ConnectionProfile.WiFi) || profiles.Contains(ConnectionProfile.Ethernet);
        if (!hasLocalNetwork)
        {
            return false;
        }

        if (!TryGetHost(apiBaseUrl, out var host))
        {
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var targetAddress) || targetAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var localNetworks = GetLocalIPv4Networks().ToList();
        foreach (var (address, mask) in localNetworks)
        {
            if (IsInSameSubnet(address, targetAddress, mask))
            {
                return true;
            }
        }

        if (IsPrivateIPv4(targetAddress))
        {
            if (localNetworks.Count == 0)
            {
                return true;
            }

            foreach (var (address, _) in localNetworks)
            {
                if (SharesPrivatePrefix(address, targetAddress))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static IReadOnlyList<MachineConnectionMode> GetAttemptOrder(
        MachineConnectionPreference preference,
        bool sameLocalNetwork)
    {
        var orderedCandidates = preference switch
        {
            MachineConnectionPreference.LocalFirst => new[] { MachineConnectionMode.LocalNetwork, MachineConnectionMode.DirectInternet, MachineConnectionMode.CloudBridge },
            MachineConnectionPreference.DirectFirst => sameLocalNetwork
                ? new[] { MachineConnectionMode.DirectInternet, MachineConnectionMode.LocalNetwork, MachineConnectionMode.CloudBridge }
                : new[] { MachineConnectionMode.DirectInternet, MachineConnectionMode.CloudBridge, MachineConnectionMode.LocalNetwork },
            MachineConnectionPreference.CloudBridgeOnly => new[] { MachineConnectionMode.CloudBridge },
            _ => new[] { MachineConnectionMode.DirectInternet, MachineConnectionMode.CloudBridge, MachineConnectionMode.LocalNetwork },
        };

        return orderedCandidates;
    }

    public static MachineConnectionMode InferMode(PairedMachineRecord record, string? apiBaseUrl)
    {
        var normalized = NormalizeUrl(apiBaseUrl);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return MachineConnectionMode.Unknown;
        }

        if (string.Equals(normalized, NormalizeUrl(record.CloudApiBaseUrl), StringComparison.OrdinalIgnoreCase))
        {
            return MachineConnectionMode.CloudBridge;
        }

        if (string.Equals(normalized, NormalizeUrl(record.LocalSecureApiBaseUrl), StringComparison.OrdinalIgnoreCase))
        {
            return MachineConnectionMode.LocalNetwork;
        }

        if (string.Equals(normalized, NormalizeUrl(record.LocalApiBaseUrl), StringComparison.OrdinalIgnoreCase))
        {
            return MachineConnectionMode.LocalNetwork;
        }

        if (string.Equals(normalized, NormalizeUrl(record.PublicApiBaseUrl), StringComparison.OrdinalIgnoreCase))
        {
            return MachineConnectionMode.DirectInternet;
        }

        return IsSameLocalNetwork(normalized)
            ? MachineConnectionMode.LocalNetwork
            : MachineConnectionMode.DirectInternet;
    }

    public static MachineConnectionMode InferActiveMode(PairedMachineRecord record)
    {
        var activeEndpoint = !string.IsNullOrWhiteSpace(record.LastConnectionEndpoint)
            ? record.LastConnectionEndpoint
            : record.ApiBaseUrl;

        var inferredMode = InferMode(record, activeEndpoint);
        return inferredMode == MachineConnectionMode.Unknown
            ? record.LastConnectionMode
            : inferredMode;
    }

    public static string GetDisplayEndpoint(PairedMachineRecord record)
    {
        return !string.IsNullOrWhiteSpace(record.LastConnectionEndpoint)
            ? record.LastConnectionEndpoint
            : (!string.IsNullOrWhiteSpace(record.ApiBaseUrl)
                ? record.ApiBaseUrl
                : record.GetCandidateApiBaseUrls().FirstOrDefault() ?? string.Empty);
    }

    private static ConnectionEndpointCandidate? BuildLocalCandidate(string? apiBaseUrl)
    {
        var normalized = NormalizeUrl(apiBaseUrl);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : new ConnectionEndpointCandidate(normalized, MachineConnectionMode.LocalNetwork);
    }

    private static string GetLocalHint(PairedMachineRecord record) =>
        !string.IsNullOrWhiteSpace(record.LocalSecureApiBaseUrl)
            ? record.LocalSecureApiBaseUrl
            : record.LocalApiBaseUrl;

    private static bool ShouldTryLocalFirst(PairedMachineRecord record, bool localAvailable) =>
        localAvailable
        && record.LastConnectionMode == MachineConnectionMode.LocalNetwork;

    private static ConnectionEndpointCandidate? BuildDirectCandidate(string? apiBaseUrl, PairedMachineRecord? record = null)
    {
        var normalized = NormalizeUrl(apiBaseUrl);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var mode = record is null
            ? MachineConnectionMode.DirectInternet
            : InferMode(record, normalized);

        return mode == MachineConnectionMode.CloudBridge
            ? null
            : new ConnectionEndpointCandidate(normalized, mode == MachineConnectionMode.Unknown ? MachineConnectionMode.DirectInternet : mode);
    }

    private static ConnectionEndpointCandidate? BuildCloudCandidate(string? apiBaseUrl)
    {
        var normalized = NormalizeUrl(apiBaseUrl);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : new ConnectionEndpointCandidate(normalized, MachineConnectionMode.CloudBridge);
    }

    private static IReadOnlyList<ConnectionEndpointCandidate> Normalize(
        IEnumerable<ConnectionEndpointCandidate?> candidates,
        PairedMachineRecord? record = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedCandidates = new List<ConnectionEndpointCandidate>();

        foreach (var candidate in candidates)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.ApiBaseUrl))
            {
                continue;
            }

            if (!seen.Add(candidate.ApiBaseUrl))
            {
                continue;
            }

            normalizedCandidates.Add(candidate);
        }

        if (record is not null && normalizedCandidates.Count == 0)
        {
            foreach (var cached in record.GetCandidateApiBaseUrls())
            {
                var normalized = NormalizeUrl(cached);
                if (string.IsNullOrWhiteSpace(normalized) || !seen.Add(normalized))
                {
                    continue;
                }

                normalizedCandidates.Add(new ConnectionEndpointCandidate(normalized, InferMode(record, normalized)));
            }
        }

        return normalizedCandidates;
    }

    private static IEnumerable<(IPAddress Address, IPAddress Mask)> GetLocalIPv4Networks()
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
                if (unicastAddress.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                    || unicastAddress.IPv4Mask is null)
                {
                    continue;
                }

                yield return (unicastAddress.Address, unicastAddress.IPv4Mask);
            }
        }
    }

    private static bool TryGetHost(string? apiBaseUrl, out string host)
    {
        host = string.Empty;
        var normalized = NormalizeUrl(apiBaseUrl);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"http://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return false;
        }

        host = uri.Host;
        return !string.IsNullOrWhiteSpace(host);
    }

    private static bool IsInSameSubnet(IPAddress left, IPAddress right, IPAddress mask)
    {
        var leftBytes = left.GetAddressBytes();
        var rightBytes = right.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        for (var index = 0; index < leftBytes.Length; index++)
        {
            if ((leftBytes[index] & maskBytes[index]) != (rightBytes[index] & maskBytes[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPrivateIPv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool SharesPrivatePrefix(IPAddress left, IPAddress right)
    {
        var leftBytes = left.GetAddressBytes();
        var rightBytes = right.GetAddressBytes();

        if (leftBytes[0] != rightBytes[0])
        {
            return false;
        }

        if (leftBytes[0] == 10)
        {
            return true;
        }

        if (leftBytes[0] == 172 && leftBytes[1] >= 16 && leftBytes[1] <= 31)
        {
            return leftBytes[1] == rightBytes[1];
        }

        if (leftBytes[0] == 192 && leftBytes[1] == 168)
        {
            return leftBytes[1] == rightBytes[1] && leftBytes[2] == rightBytes[2];
        }

        return false;
    }

    private static string NormalizeUrl(string? apiBaseUrl) => apiBaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
}

public sealed record ConnectionEndpointCandidate(string ApiBaseUrl, MachineConnectionMode Mode);
