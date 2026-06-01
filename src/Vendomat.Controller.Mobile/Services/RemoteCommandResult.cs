using Vendomat.Controller.Mobile.Models;

namespace Vendomat.Controller.Mobile.Services;

public sealed class RemoteCommandResult
{
    public required string ApiBaseUrl { get; init; }
    public required MachineConnectionMode ConnectionMode { get; init; }
}
