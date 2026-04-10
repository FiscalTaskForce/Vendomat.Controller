using Vendomat.Controller.Mobile.Models;

namespace Vendomat.Controller.Mobile.Services;

public static class ManualPairingLaunchBridge
{
    private static readonly object SyncRoot = new();
    private static ManualPairingLaunchRequest? _pendingRequest;

    public static event EventHandler? PendingRequestChanged;

    public static void Publish(ManualPairingLaunchRequest request)
    {
        if (request is null || !request.HasAnyValue)
        {
            return;
        }

        lock (SyncRoot)
        {
            _pendingRequest = request;
        }

        PendingRequestChanged?.Invoke(null, EventArgs.Empty);
    }

    public static bool TryConsume(out ManualPairingLaunchRequest? request)
    {
        lock (SyncRoot)
        {
            request = _pendingRequest;
            _pendingRequest = null;
            return request is not null;
        }
    }
}
