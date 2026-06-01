using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Mobile.Models;

namespace Vendomat.Controller.Mobile.Services;

public sealed class RemoteCreditResult
{
    public required string ApiBaseUrl { get; init; }
    public required MachineConnectionMode ConnectionMode { get; init; }
    public MachineStatusSnapshot? Snapshot { get; init; }
    public bool IsQueued { get; init; }
    public Guid? CommandId { get; init; }
}
