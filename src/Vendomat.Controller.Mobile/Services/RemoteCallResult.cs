using Vendomat.Controller.Mobile.Models;

namespace Vendomat.Controller.Mobile.Services;

public sealed class RemoteCallResult<T>
{
    public required string ApiBaseUrl { get; init; }
    public required MachineConnectionMode ConnectionMode { get; init; }
    public required T Payload { get; init; }
}
