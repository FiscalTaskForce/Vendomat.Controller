using Vendomat.Controller.Application.Interfaces;

namespace Vendomat.Controller.Tablet.Services;

public sealed class KioskDisplayService : IKioskDisplayService
{
    public Task EnterImmersiveModeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
