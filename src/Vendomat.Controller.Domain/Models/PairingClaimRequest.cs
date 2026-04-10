namespace Vendomat.Controller.Domain.Models;

public sealed class PairingClaimRequest
{
    public Guid MachineId { get; set; }
    public string PairingCode { get; set; } = string.Empty;
}
