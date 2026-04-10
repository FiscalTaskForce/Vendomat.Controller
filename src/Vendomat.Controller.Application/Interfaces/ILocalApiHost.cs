namespace Vendomat.Controller.Application.Interfaces;

public interface ILocalApiHost
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
