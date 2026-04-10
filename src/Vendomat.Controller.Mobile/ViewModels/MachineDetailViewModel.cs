using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vendomat.Controller.Client.Localization;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Mobile.Models;
using Vendomat.Controller.Mobile.Services;

namespace Vendomat.Controller.Mobile.ViewModels;

public partial class MachineDetailViewModel(
    PairedMachineStore pairedMachineStore,
    VendomatRemoteClient remoteClient,
    LanguageService languageService) : ObservableObject
{
    private Guid _machineId;
    private PairedMachineRecord? _record;
    private MachineStatusSnapshot? _snapshot;
    private MachineDashboardSnapshot? _dashboard;
    private CancellationTokenSource? _refreshLoopCts;

    [ObservableProperty]
    private string machineName = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string temperatureText = "-";

    [ObservableProperty]
    private string stockText = "-";

    [ObservableProperty]
    private string priceText = "-";

    [ObservableProperty]
    private string creditText = "-";

    [ObservableProperty]
    private string paymentText = "-";

    [ObservableProperty]
    private string activityText = "-";

    [ObservableProperty]
    private string contactText = "-";

    [ObservableProperty]
    private string apiBaseUrlText = "-";

    [ObservableProperty]
    private string updatedAtText = string.Empty;

    [ObservableProperty]
    private string todayRevenueText = "0.00 RON";

    [ObservableProperty]
    private string todayLitersText = "0.00 L";

    [ObservableProperty]
    private string todaySalesCountText = "0";

    [ObservableProperty]
    private string totalRevenueText = "0.00 RON";

    [ObservableProperty]
    private string totalLitersText = "0.00 L";

    [ObservableProperty]
    private string totalSalesCountText = "0";

    [ObservableProperty]
    private string lastSaleText = "-";

    [ObservableProperty]
    private string lastSanitationText = "-";

    [ObservableProperty]
    private string sanitationCountText = "0";

    [ObservableProperty]
    private string remoteCreditAmountText = "10";

    [ObservableProperty]
    private bool isBusy;

    public ObservableCollection<DashboardListItem> RecentSales { get; } = [];

    public ObservableCollection<DashboardListItem> RecentSanitations { get; } = [];

    public bool HasSalesHistory => RecentSales.Count > 0;

    public bool HasSanitationHistory => RecentSanitations.Count > 0;

    public async Task LoadAsync(Guid machineId)
    {
        languageService.LanguageChanged -= OnLanguageChanged;
        languageService.LanguageChanged += OnLanguageChanged;
        _refreshLoopCts?.Cancel();
        _refreshLoopCts = new CancellationTokenSource();

        _machineId = machineId;
        _record = await pairedMachineStore.GetAsync(machineId);
        if (_record is null)
        {
            return;
        }

        MachineName = _record.MachineName;
        ApiBaseUrlText = _record.ApiBaseUrl;
        await RefreshDashboardAsync();
        _ = RunRefreshLoopAsync(_refreshLoopCts.Token);
    }

    public void Stop()
    {
        _refreshLoopCts?.Cancel();
        _refreshLoopCts = null;
        languageService.LanguageChanged -= OnLanguageChanged;
    }

    [RelayCommand]
    private Task OpenSettings() => Shell.Current.GoToAsync($"MachineSettingsPage?machineId={_machineId}");

    [RelayCommand]
    private Task Refresh() => RefreshDashboardAsync();

    [RelayCommand]
    private Task RunContinuousCleaning() => RunCleaningAsync(SanitationMode.Continuous, TimeSpan.FromSeconds(20), TimeSpan.Zero, TimeSpan.Zero);

    [RelayCommand]
    private Task RunPulsedCleaning() => RunCleaningAsync(SanitationMode.Pulsed, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

    [RelayCommand]
    private async Task AddRemoteCredit()
    {
        if (_record is null)
        {
            return;
        }

        var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;

        if (!TryParseRemoteCreditAmount(RemoteCreditAmountText, out var amount) || amount <= 0)
        {
            if (page is not null)
            {
                await page.DisplayAlertAsync(
                T(nameof(AppLanguageStrings.MobileDetailRemoteCreditTitle)),
                T(nameof(AppLanguageStrings.MobileDetailRemoteCreditInvalid)),
                T(nameof(AppLanguageStrings.CommonConfirm)));
            }
            return;
        }

        IsBusy = true;
        try
        {
            var result = await remoteClient.AddRemoteCreditAsync(_record, Math.Round(amount, 2));
            _record.ApiBaseUrl = result.ApiBaseUrl;
            ApiBaseUrlText = result.ApiBaseUrl;

            if (result.Snapshot is not null)
            {
                ApplySnapshot(result.Snapshot);
                await UpdateRecordAsync(result.Snapshot);
                await RefreshDashboardAfterCreditAsync();
                return;
            }

            if (result.IsQueued)
            {
                if (page is not null)
                {
                    await page.DisplayAlertAsync(
                        T(nameof(AppLanguageStrings.MobileDetailRemoteCreditTitle)),
                        T(nameof(AppLanguageStrings.MobileDetailRemoteCreditQueued)),
                        T(nameof(AppLanguageStrings.CommonConfirm)));
                }

                await RefreshDashboardAfterCreditAsync();
                return;
            }

            await RefreshDashboardAfterCreditAsync();
        }
        catch (Exception ex)
        {
            if (page is not null)
            {
                await page.DisplayAlertAsync(
                    T(nameof(AppLanguageStrings.MobileDetailRemoteCreditTitle)),
                    $"{T(nameof(AppLanguageStrings.MobileDetailRemoteCreditError))}\n{ex.Message}",
                    T(nameof(AppLanguageStrings.CommonConfirm)));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool TryParseRemoteCreditAmount(string? rawValue, out decimal amount)
    {
        amount = 0;
        var normalized = rawValue?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.CurrentCulture, out amount))
        {
            return true;
        }

        return decimal.TryParse(
            normalized.Replace(',', '.'),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out amount);
    }

    private async Task RefreshDashboardAfterCreditAsync()
    {
        await Task.Delay(1200);
        await RefreshDashboardAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_record is null)
        {
            return;
        }

        try
        {
            var (apiBaseUrl, snapshot) = await remoteClient.GetStatusAsync(_record);
            _record.ApiBaseUrl = apiBaseUrl;
            ApiBaseUrlText = apiBaseUrl;
            _snapshot = snapshot;
            ApplySnapshot(snapshot);
            await UpdateRecordAsync(snapshot);
        }
        catch
        {
            StatusText = T(nameof(AppLanguageStrings.MobileDevicesStatusOffline));
        }
    }

    private async Task RefreshDashboardAsync()
    {
        if (_record is null)
        {
            return;
        }

        try
        {
            var (apiBaseUrl, dashboard) = await remoteClient.GetDashboardAsync(_record);
            _record.ApiBaseUrl = apiBaseUrl;
            ApiBaseUrlText = apiBaseUrl;
            _dashboard = dashboard;
            _snapshot = dashboard.Status;
            ApplyDashboard(dashboard);
            await UpdateRecordAsync(dashboard.Status);
        }
        catch
        {
            await RefreshStatusAsync();
        }
    }

    private async Task RunCleaningAsync(SanitationMode mode, TimeSpan duration, TimeSpan pulseOn, TimeSpan pulseOff)
    {
        if (_record is null)
        {
            return;
        }

        _record.ApiBaseUrl = await remoteClient.RunSanitationAsync(_record, new Application.Contracts.SanitationRequest
        {
            Mode = mode,
            Duration = duration,
            PulseOn = pulseOn,
            PulseOff = pulseOff,
        });
        ApiBaseUrlText = _record.ApiBaseUrl;

        await RefreshDashboardAsync();
    }

    private void ApplyDashboard(MachineDashboardSnapshot dashboard)
    {
        ApplySnapshot(dashboard.Status);

        TodayRevenueText = $"{dashboard.Sales.TodayRevenue:0.00} RON";
        TodayLitersText = $"{dashboard.Sales.TodayLiters:0.##} L";
        TodaySalesCountText = dashboard.Sales.TodayCompletedSales.ToString();
        TotalRevenueText = $"{dashboard.Sales.TotalRevenue:0.00} RON";
        TotalLitersText = $"{dashboard.Sales.TotalLiters:0.##} L";
        TotalSalesCountText = dashboard.Sales.TotalCompletedSales.ToString();
        LastSaleText = dashboard.Sales.LastSaleAtUtc is null
            ? "-"
            : dashboard.Sales.LastSaleAtUtc.Value.LocalDateTime.ToString("g");
        LastSanitationText = dashboard.Sanitation.LastSanitationAtUtc is null
            ? "-"
            : dashboard.Sanitation.LastSanitationAtUtc.Value.LocalDateTime.ToString("g");
        SanitationCountText = $"{dashboard.Sanitation.TotalCycles} / 7z: {dashboard.Sanitation.CyclesLast7Days}";

        RecentSales.Clear();
        foreach (var sale in dashboard.RecentSales)
        {
            RecentSales.Add(new DashboardListItem
            {
                Title = $"{sale.DispensedLiters:0.##} L · {sale.TotalAmount:0.00} RON",
                Subtitle = $"{ResolvePaymentText(sale.PaymentMethod)} · {ResolveSaleStatusText(sale.Status)} · {(sale.CompletedAtUtc ?? sale.StartedAtUtc).LocalDateTime:g}",
                Value = $"{sale.PricePerLiter:0.00} RON/L",
            });
        }

        RecentSanitations.Clear();
        foreach (var sanitation in dashboard.RecentSanitations)
        {
            RecentSanitations.Add(new DashboardListItem
            {
                Title = ResolveSanitationModeText(sanitation.Mode),
                Subtitle = $"{sanitation.StartedAtUtc.LocalDateTime:g} · {sanitation.Duration.TotalSeconds:0}s",
                Value = string.IsNullOrWhiteSpace(sanitation.Notes) ? "-" : sanitation.Notes,
            });
        }

        OnPropertyChanged(nameof(HasSalesHistory));
        OnPropertyChanged(nameof(HasSanitationHistory));
    }

    private void ApplySnapshot(MachineStatusSnapshot snapshot)
    {
        MachineName = snapshot.Settings.MachineName;
        StatusText = snapshot.Session.ActivityState switch
        {
            MachineActivityState.Dispensing => T(nameof(AppLanguageStrings.MobileDetailDispensing)),
            MachineActivityState.Cleaning => T(nameof(AppLanguageStrings.MobileDetailCleaning)),
            MachineActivityState.OutOfService => T(nameof(AppLanguageStrings.MobileDetailOutOfService)),
            MachineActivityState.Ready => T(nameof(AppLanguageStrings.MobileDetailReady)),
            _ => T(nameof(AppLanguageStrings.MobileDetailUnknown)),
        };

        TemperatureText = $"{snapshot.Sensor.TemperatureCelsius:0.0} °C";
        StockText = $"{snapshot.Settings.CurrentStockLiters:0.##} / {snapshot.Settings.TankCapacityLiters:0.##} L";
        PriceText = $"{snapshot.Settings.PricePerLiter:0.00} RON";
        CreditText = $"{snapshot.Session.CurrentCreditAmount:0.00} RON";
        PaymentText = ResolvePaymentText(snapshot.Session.ActivePaymentMethod);
        ActivityText = StatusText;
        ContactText = string.IsNullOrWhiteSpace(snapshot.Settings.ContactPhone)
            ? T(nameof(AppLanguageStrings.DashboardContactMissing))
            : snapshot.Settings.ContactPhone;
        UpdatedAtText = string.Format(
            T(nameof(AppLanguageStrings.MobileDetailUpdatedAtFormat)),
            snapshot.GeneratedAtUtc.LocalDateTime);
    }

    private async Task UpdateRecordAsync(MachineStatusSnapshot snapshot)
    {
        if (_record is null)
        {
            return;
        }

        _record.MachineName = snapshot.Settings.MachineName;
        _record.LocalApiBaseUrl = snapshot.Settings.LocalApiBaseUrl;
        _record.PublicApiBaseUrl = snapshot.Settings.PublicApiBaseUrl;
        _record.CloudApiBaseUrl = snapshot.Settings.CloudApiBaseUrl;
        _record.LastSeenOnline = true;
        _record.LastSeenUtc = snapshot.GeneratedAtUtc;
        _record.LastKnownStockLiters = snapshot.Settings.CurrentStockLiters;
        _record.LastKnownTemperatureCelsius = snapshot.Sensor.TemperatureCelsius;
        _record.LastKnownPricePerLiter = snapshot.Settings.PricePerLiter;
        await pairedMachineStore.AddOrUpdateAsync(_record);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_dashboard is not null)
        {
            ApplyDashboard(_dashboard);
            return;
        }

        if (_snapshot is not null)
        {
            ApplySnapshot(_snapshot);
        }
    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        var tick = 0;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                tick++;
                await MainThread.InvokeOnMainThreadAsync(RefreshStatusAsync);
                if (tick % 10 == 0)
                {
                    await MainThread.InvokeOnMainThreadAsync(RefreshDashboardAsync);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string ResolvePaymentText(PaymentMethod? paymentMethod) => paymentMethod == PaymentMethod.Card
        ? T(nameof(AppLanguageStrings.DashboardPaymentCard))
        : T(nameof(AppLanguageStrings.DashboardPaymentCash));

    private string ResolveSaleStatusText(SaleStatus status) => status switch
    {
        SaleStatus.Completed => T(nameof(AppLanguageStrings.MobileDetailSaleStatusCompleted)),
        SaleStatus.Failed => T(nameof(AppLanguageStrings.MobileDetailSaleStatusFailed)),
        _ => T(nameof(AppLanguageStrings.MobileDetailSaleStatusPending)),
    };

    private string ResolveSanitationModeText(SanitationMode mode) => mode == SanitationMode.Pulsed
        ? T(nameof(AppLanguageStrings.MobileDetailSanitationPulsed))
        : T(nameof(AppLanguageStrings.MobileDetailSanitationContinuous));

    private string T(string key) => languageService.GetText(key);
}
