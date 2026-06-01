namespace Vendomat.Controller.Domain.Models;

public sealed class CloudMachineCompanionSessionsRequest
{
    public Guid MachineId { get; set; }
    public string MachineToken { get; set; } = string.Empty;
}
