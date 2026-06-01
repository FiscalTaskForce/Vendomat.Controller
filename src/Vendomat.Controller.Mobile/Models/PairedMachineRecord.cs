namespace Vendomat.Controller.Mobile.Models;

public sealed class PairedMachineRecord
{
    public Guid MachineId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string LocalApiBaseUrl { get; set; } = string.Empty;
    public string LocalSecureApiBaseUrl { get; set; } = string.Empty;
    public string LocalCertificateFingerprint { get; set; } = string.Empty;
    public string PublicApiBaseUrl { get; set; } = string.Empty;
    public string CloudApiBaseUrl { get; set; } = string.Empty;
    public string CompanionAccessToken { get; set; } = string.Empty;
    public string PairingCode { get; set; } = string.Empty;
    public MachineConnectionPreference PreferredConnectionPreference { get; set; } = MachineConnectionPreference.Auto;
    public MachineConnectionMode LastConnectionMode { get; set; }
    public string LastConnectionEndpoint { get; set; } = string.Empty;
    public DateTimeOffset? LastConnectionCheckedUtc { get; set; }
    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenUtc { get; set; }
    public bool LastSeenOnline { get; set; }
    public decimal LastKnownStockLiters { get; set; }
    public float LastKnownTemperatureCelsius { get; set; }
    public decimal LastKnownPricePerLiter { get; set; }

    public IEnumerable<string> GetCandidateApiBaseUrls()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { ApiBaseUrl, LastConnectionEndpoint, LocalSecureApiBaseUrl, LocalApiBaseUrl, PublicApiBaseUrl, CloudApiBaseUrl })
        {
            var normalized = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    public void RememberSuccessfulConnection(string? apiBaseUrl, MachineConnectionMode mode)
    {
        if (!string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            ApiBaseUrl = apiBaseUrl.Trim();
            LastConnectionEndpoint = ApiBaseUrl;
        }

        LastConnectionMode = mode;
        LastConnectionCheckedUtc = DateTimeOffset.UtcNow;
    }
}
