using Vendomat.Controller.Mobile.Models;
using Vendomat.Controller.Mobile.Services;
using Vendomat.Controller.Mobile.ViewModels;
using System.Diagnostics;

namespace Vendomat.Controller.Mobile;

public partial class MainPage : ContentPage
{
    private MainPageViewModel ViewModel => (MainPageViewModel)BindingContext;
    private bool _manualLaunchSubscribed;
    private CancellationTokenSource? _refreshLoopCts;

    public MainPage()
    {
        InitializeComponent();
        BindingContext = ServiceRegistry.GetRequiredService<MainPageViewModel>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        EnsureManualLaunchSubscription();
        _refreshLoopCts?.Cancel();
        _refreshLoopCts = new CancellationTokenSource();
        await ViewModel.LoadAsync();
        await ViewModel.RefreshCommand.ExecuteAsync(null);
        await ConsumePendingManualLaunchAsync();
        _ = RunRefreshLoopAsync(_refreshLoopCts.Token);
    }

    protected override void OnDisappearing()
    {
        _refreshLoopCts?.Cancel();
        _refreshLoopCts = null;

        if (_manualLaunchSubscribed)
        {
            ManualPairingLaunchBridge.PendingRequestChanged -= OnPendingManualLaunchChanged;
            _manualLaunchSubscribed = false;
        }

        base.OnDisappearing();
    }

    private void EnsureManualLaunchSubscription()
    {
        if (_manualLaunchSubscribed)
        {
            return;
        }

        ManualPairingLaunchBridge.PendingRequestChanged += OnPendingManualLaunchChanged;
        _manualLaunchSubscribed = true;
    }

    private async void OnPendingManualLaunchChanged(object? sender, EventArgs e) =>
        await MainThread.InvokeOnMainThreadAsync(ConsumePendingManualLaunchAsync);

    private async Task ConsumePendingManualLaunchAsync()
    {
        if (!ManualPairingLaunchBridge.TryConsume(out var request) || request is null)
        {
            return;
        }

        Debug.WriteLine($"[ManualPairing] Consume pending request AutoSubmit={request.AutoSubmit} MachineId={request.MachineId}");
        if (request.AutoSubmit && request.HasAnyValue)
        {
            var pairingViewModel = ServiceRegistry.GetRequiredService<QrScannerViewModel>();
            if (await pairingViewModel.ApplyManualLaunchRequestAsync(request))
            {
                Debug.WriteLine("[ManualPairing] Auto pairing succeeded from MainPage.");
                await ViewModel.LoadAsync();
                return;
            }

            Debug.WriteLine("[ManualPairing] Auto pairing did not complete, navigating to scanner page.");
        }

        await Shell.Current.GoToAsync(BuildScannerRoute(request));
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await MainThread.InvokeOnMainThreadAsync(() => ViewModel.RefreshCommand.ExecuteAsync(null));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string BuildScannerRoute(ManualPairingLaunchRequest request)
    {
        var query = new List<string>();
        AppendQuery(query, "manualMode", "1");
        AppendQuery(query, "autoSubmit", request.AutoSubmit ? "1" : "0");
        AppendQuery(query, "rawPayload", request.RawPayload);
        AppendQuery(query, "machineId", request.MachineId);
        AppendQuery(query, "pairingCode", request.PairingCode);
        AppendQuery(query, "cloudApiBaseUrl", request.CloudApiBaseUrl);
        AppendQuery(query, "publicApiBaseUrl", request.PublicApiBaseUrl);
        AppendQuery(query, "localApiBaseUrl", request.LocalApiBaseUrl);

        return query.Count == 0
            ? "QrScannerPage"
            : $"QrScannerPage?{string.Join("&", query)}";
    }

    private static void AppendQuery(ICollection<string> query, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        query.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
    }
}
