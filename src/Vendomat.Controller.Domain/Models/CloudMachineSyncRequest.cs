namespace Vendomat.Controller.Domain.Models;

public sealed class CloudMachineSyncRequest
{
    public Guid MachineId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string MachineToken { get; set; } = string.Empty;
    public string CompanionAccessToken { get; set; } = string.Empty;
    public string LocalApiBaseUrl { get; set; } = string.Empty;
    public string PublicApiBaseUrl { get; set; } = string.Empty;
    public string CloudApiBaseUrl { get; set; } = string.Empty;
    public MachineStatusSnapshot Snapshot { get; set; } = new();
    public MachineDashboardSnapshot? Dashboard { get; set; }
}
