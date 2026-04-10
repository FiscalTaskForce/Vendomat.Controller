namespace Vendomat.Controller.Mobile.Models;

public sealed class ManualPairingLaunchRequest
{
    public string RawPayload { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string PairingCode { get; set; } = string.Empty;
    public string CloudApiBaseUrl { get; set; } = string.Empty;
    public string PublicApiBaseUrl { get; set; } = string.Empty;
    public string LocalApiBaseUrl { get; set; } = string.Empty;
    public bool AutoSubmit { get; set; }

    public bool HasAnyValue =>
        !string.IsNullOrWhiteSpace(RawPayload)
        || !string.IsNullOrWhiteSpace(MachineId)
        || !string.IsNullOrWhiteSpace(PairingCode)
        || !string.IsNullOrWhiteSpace(CloudApiBaseUrl)
        || !string.IsNullOrWhiteSpace(PublicApiBaseUrl)
        || !string.IsNullOrWhiteSpace(LocalApiBaseUrl);
}
