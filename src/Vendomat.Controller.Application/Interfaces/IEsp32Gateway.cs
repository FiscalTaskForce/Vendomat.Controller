using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Application.Interfaces;

public interface IEsp32Gateway
{
    event Action<SensorSnapshot>? SensorSnapshotReceived;
    event Action<decimal>? DispenseProgressReceived;
    event Action? DispenseCompleted;
    event Action<string>? PortDetected;

    Task StartAsync(string preferredPortName, int baudRate, bool autoDiscover, CancellationToken cancellationToken = default);
    Task SendDispenseRequestAsync(decimal targetLiters, int pulsesPerLiter, CancellationToken cancellationToken = default);
    Task SendSanitationAsync(SanitationMode mode, TimeSpan duration, TimeSpan pulseOn, TimeSpan pulseOff, CancellationToken cancellationToken = default);
    Task StopDispenseAsync(CancellationToken cancellationToken = default);
}
