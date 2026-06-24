using Vendomat.Controller.Mobile.Models;
using Vendomat.Controller.Mobile.Services;
using Vendomat.Controller.Mobile.ViewModels;
using System.Diagnostics;

namespace Vendomat.Controller.Mobile;

public partial class MainPage : ContentPage
{
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

        try
        {
            var viewModel = EnsureViewModel();
            EnsureManualLaunchSubscription();

            _refreshLoopCts?.Cancel();
            var refreshLoopCts = new CancellationTokenSource();
            _refreshLoopCts = refreshLoopCts;

            await viewModel.LoadAsync();
            if (refreshLoopCts.IsCancellationRequested)
            {
                return;
            }

            await viewModel.RefreshSilentlyAsync();
            if (refreshLoopCts.IsCancellationRequested)
            {
                return;
            }

            await ConsumePendingManualLaunchAsync();
            if (_refreshLoopCts != refreshLoopCts || refreshLoopCts.IsCancellationRequested)
            {
                return;
            }

            _ = RunRefreshLoopAsync(refreshLoopCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainPage] OnAppearing failed: {ex}");
        }
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

    private async void OnPendingManualLaunchChanged(object? sender, EventArgs e)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(ConsumePendingManualLaunchAsync);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainPage] Manual launch handling failed: {ex}");
        }
    }

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
                await EnsureViewModel().LoadAsync();
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
                await MainThread.InvokeOnMainThreadAsync(() => EnsureViewModel().RefreshSilentlyAsync());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainPage] Refresh loop failed: {ex}");
        }
    }

    private MainPageViewModel EnsureViewModel()
    {
        if (BindingContext is MainPageViewModel viewModel)
        {
            return viewModel;
        }

        viewModel = ServiceRegistry.GetRequiredService<MainPageViewModel>();
        BindingContext = viewModel;
        return viewModel;
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
