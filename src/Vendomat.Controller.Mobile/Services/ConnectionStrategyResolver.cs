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
        var directFallback = BuildDirectCandidate(record.PublicApiBaseUrl, record);
        var cloudFallback = BuildCloudCandidate(record.CloudApiBaseUrl);
        var localFallback = BuildLocalCandidate(localHint);
        List<ConnectionEndpointCandidate?> candidates = record.PreferredConnectionPreference switch
        {
            MachineConnectionPreference.LocalFirst => [localFallback, directFallback, cloudFallback],
            MachineConnectionPreference.DirectFirst => [directFallback, localAvailable ? localFallback : null, cloudFallback, localAvailable ? null : localFallback],
            MachineConnectionPreference.CloudBridgeOnly => [cloudFallback],
            _ => localAvailable
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
        var localAvailable = IsSameLocalNetwork(localHint);
        var candidates = localAvailable
            ? new ConnectionEndpointCandidate?[]
            {
                BuildLocalCandidate(localHint),
                BuildDirectCandidate(payload.PublicApiBaseUrl),
                BuildCloudCandidate(payload.CloudApiBaseUrl),
            }
            : new ConnectionEndpointCandidate?[]
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

        foreach (var (address, mask) in GetLocalIPv4Networks())
        {
            if (IsInSameSubnet(address, targetAddress, mask))
            {
                return true;
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
            _ => sameLocalNetwork
                ? new[] { MachineConnectionMode.LocalNetwork, MachineConnectionMode.DirectInternet, MachineConnectionMode.CloudBridge }
                : new[] { MachineConnectionMode.DirectInternet, MachineConnectionMode.CloudBridge, MachineConnectionMode.LocalNetwork },
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

    private static ConnectionEndpointCandidate? BuildLocalCandidate(string? apiBaseUrl)
    {
        var normalized = NormalizeUrl(apiBaseUrl);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : new ConnectionEndpointCandidate(normalized, MachineConnectionMode.LocalNetwork);
    }

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

    private static string NormalizeUrl(string? apiBaseUrl) => apiBaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
}

public sealed record ConnectionEndpointCandidate(string ApiBaseUrl, MachineConnectionMode Mode);
