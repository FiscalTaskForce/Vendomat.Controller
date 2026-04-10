namespace Vendomat.Controller.Application.Interfaces;

public interface IKioskDisplayService
{
    Task EnterImmersiveModeAsync(CancellationToken cancellationToken = default);
}
