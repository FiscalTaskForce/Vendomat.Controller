namespace Vendomat.Controller.Domain.Models;

public sealed class CloudCompanionSessionRevokeRequest
{
    public Guid MachineId { get; set; }
    public string MachineToken { get; set; } = string.Empty;
    public string CompanionTokenPrefix { get; set; } = string.Empty;
}
