using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Client.Localization;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Security;
using Vendomat.Controller.Tablet.Models;

namespace Vendomat.Controller.Tablet.ViewModels;

public partial class DashboardViewModel(
    IMachineRuntimeService machineRuntimeService,
    IAdvertisementRepository advertisementRepository,
    LanguageService languageService) : ObservableObject
{
    private CancellationTokenSource? _refreshCancellationTokenSource;
    private MachineStatusSnapshot? _lastSnapshot;
    private int _promoTick;

    [ObservableProperty]
    private string machineName = string.Empty;

    [ObservableProperty]
    private decimal pricePerLiter;

    [ObservableProperty]
    private decimal currentStockLiters;

    [ObservableProperty]
    private decimal tankCapacityLiters;

    [ObservableProperty]
    private float temperatureCelsius;

    [ObservableProperty]
    private decimal currentCredit;

    [ObservableProperty]
    private decimal requestedLiters;

    [ObservableProperty]
    private decimal totalAmount;

    [ObservableProperty]
    private decimal dispensedLiters;

    [ObservableProperty]
    private bool canUseCash;

    [ObservableProperty]
    private bool canUseCard;

    [ObservableProperty]
    private bool isDispensing;

    [ObservableProperty]
    private string contactValue = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private PaymentMethod selectedPaymentMethod = PaymentMethod.Cash;

    [ObservableProperty]
    private int currentPromoIndex;

    [ObservableProperty]
    private ObservableCollection<PromoSlide> promoSlides = [];

    [ObservableProperty]
    private LanguageOption? selectedLanguageOption;

    public IReadOnlyList<LanguageOption> AvailableLanguages => languageService.AvailableLanguages;

    public decimal DispenseProgress => RequestedLiters <= 0 ? 0 : Math.Clamp(DispensedLiters / RequestedLiters, 0m, 1m);

    public bool CanAdjustLiters => SelectedPaymentMethod == PaymentMethod.Card && !IsDispensing && CanUseCard;

    public bool CanStartDispense => !IsDispensing && RequestedLiters > 0;

    public string StartButtonText => IsDispensing
        ? T(nameof(AppLanguageStrings.DashboardDispensingButton))
        : T(nameof(AppLanguageStrings.DashboardStartDispense));

    public string TankCapacityDisplay => string.Format(
        T(nameof(AppLanguageStrings.DashboardStockCapacityFormat)),
        TankCapacityLiters);

    public string SelectedTotalDisplay => string.Format(
        T(nameof(AppLanguageStrings.DashboardSelectedTotalFormat)),
        TotalAmount);

    public string ContactDisplay => string.Format(
        T(nameof(AppLanguageStrings.DashboardContactFormat)),
        string.IsNullOrWhiteSpace(ContactValue)
            ? T(nameof(AppLanguageStrings.DashboardContactMissing))
            : ContactValue);

    public async Task StartAsync()
    {
        if (_refreshCancellationTokenSource is not null)
        {
            return;
        }

        languageService.LanguageChanged += OnLanguageChanged;
        SelectedLanguageOption = languageService.GetCurrentLanguageOption();

        await LoadAdvertisementsAsync();
        await LoadStatusAsync();

        _refreshCancellationTokenSource = new CancellationTokenSource();
        _ = RefreshLoopAsync(_refreshCancellationTokenSource.Token);
    }

    public async Task<bool> VerifyAdminPasscodeAsync(string passcode)
    {
        var settings = await machineRuntimeService.GetSettingsAsync();
        return AdminPasscodeHasher.Verify(settings.AdminPasscodeHash, passcode);
    }

    public Task StopAsync()
    {
        _refreshCancellationTokenSource?.Cancel();
        _refreshCancellationTokenSource = null;
        languageService.LanguageChanged -= OnLanguageChanged;
        return Task.CompletedTask;
    }

    partial void OnSelectedLanguageOptionChanged(LanguageOption? value)
    {
        if (value is null || value.Code == languageService.CurrentLocale)
        {
            return;
        }

        languageService.SetLanguage(value.Code);
    }

    partial void OnSelectedPaymentMethodChanged(PaymentMethod value) => OnPropertyChanged(nameof(CanAdjustLiters));

    partial void OnCanUseCardChanged(bool value) => OnPropertyChanged(nameof(CanAdjustLiters));

    partial void OnRequestedLitersChanged(decimal value)
    {
        OnPropertyChanged(nameof(CanStartDispense));
        OnPropertyChanged(nameof(DispenseProgress));
        OnPropertyChanged(nameof(SelectedTotalDisplay));
    }

    partial void OnDispensedLitersChanged(decimal value) => OnPropertyChanged(nameof(DispenseProgress));

    partial void OnTotalAmountChanged(decimal value) => OnPropertyChanged(nameof(SelectedTotalDisplay));

    partial void OnTankCapacityLitersChanged(decimal value) => OnPropertyChanged(nameof(TankCapacityDisplay));

    partial void OnContactValueChanged(string value) => OnPropertyChanged(nameof(ContactDisplay));

    partial void OnIsDispensingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAdjustLiters));
        OnPropertyChanged(nameof(CanStartDispense));
        OnPropertyChanged(nameof(StartButtonText));
    }

    [RelayCommand]
    private async Task SelectCash()
    {
        if (!CanUseCash)
        {
            return;
        }

        try
        {
            await machineRuntimeService.SetPaymentMethodAsync(PaymentMethod.Cash);
            StatusMessage = T(nameof(AppLanguageStrings.DashboardStatusCashSelected));
            await LoadStatusAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SelectCard()
    {
        if (!CanUseCard)
        {
            return;
        }

        try
        {
            await machineRuntimeService.SetPaymentMethodAsync(PaymentMethod.Card);
            StatusMessage = T(nameof(AppLanguageStrings.DashboardStatusCardSelected));
            await LoadStatusAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task IncreaseRequestedLiters()
    {
        if (!CanAdjustLiters)
        {
            return;
        }

        await machineRuntimeService.SetRequestedLitersAsync(RequestedLiters + 0.5m);
        await LoadStatusAsync();
    }

    [RelayCommand]
    private async Task DecreaseRequestedLiters()
    {
        if (!CanAdjustLiters)
        {
            return;
        }

        await machineRuntimeService.SetRequestedLitersAsync(Math.Max(0, RequestedLiters - 0.5m));
        await LoadStatusAsync();
    }

    [RelayCommand]
    private async Task StartDispense()
    {
        try
        {
            await machineRuntimeService.StartDispenseAsync(new DispenseCommand
            {
                RequestedLiters = RequestedLiters,
                PaymentMethod = SelectedPaymentMethod,
                CreditAmount = CurrentCredit,
            });

            StatusMessage = T(nameof(AppLanguageStrings.DashboardStatusDispenseStarted));
            await LoadStatusAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await MainThread.InvokeOnMainThreadAsync(LoadStatusAsync);
                RotateSlides();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadAdvertisementsAsync()
    {
        var assets = await advertisementRepository.GetAllAsync();
        PromoSlides = new ObservableCollection<PromoSlide>(assets
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.SortOrder)
            .Select(x => new PromoSlide
            {
                Title = x.Title,
                Subtitle = x.Subtitle,
                Badge = x.Badge,
            }));
    }

    private async Task LoadStatusAsync()
    {
        var snapshot = await machineRuntimeService.GetStatusAsync();
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(MachineStatusSnapshot snapshot)
    {
        _lastSnapshot = snapshot;

        MachineName = snapshot.Settings.MachineName;
        PricePerLiter = snapshot.Settings.PricePerLiter;
        CurrentStockLiters = snapshot.Settings.CurrentStockLiters;
        TankCapacityLiters = snapshot.Settings.TankCapacityLiters;
        TemperatureCelsius = snapshot.Sensor.TemperatureCelsius;
        CurrentCredit = snapshot.Session.CurrentCreditAmount;
        RequestedLiters = snapshot.Session.RequestedLiters;
        TotalAmount = snapshot.Session.TotalAmount;
        DispensedLiters = snapshot.Session.DispensedLiters;
        IsDispensing = snapshot.Session.ActivityState == MachineActivityState.Dispensing;

        var isMachineBusy = snapshot.Session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning;
        CanUseCash = snapshot.Settings.CashPaymentEnabled && !isMachineBusy;
        CanUseCard = snapshot.Settings.CardPaymentEnabled
            && !isMachineBusy
            && !snapshot.Session.IsCardSelectionBlocked
            && snapshot.Session.CurrentCreditAmount <= 0;
        ContactValue = snapshot.Settings.ContactPhone;

        if (snapshot.Session.ActivePaymentMethod is not null)
        {
            SelectedPaymentMethod = snapshot.Session.ActivePaymentMethod.Value;
        }
        else if (CanUseCash)
        {
            SelectedPaymentMethod = PaymentMethod.Cash;
        }
        else if (CanUseCard)
        {
            SelectedPaymentMethod = PaymentMethod.Card;
        }

        StatusMessage = ResolveStatusMessage(snapshot);
        OnPropertyChanged(nameof(ContactDisplay));
        OnPropertyChanged(nameof(TankCapacityDisplay));
        OnPropertyChanged(nameof(SelectedTotalDisplay));
    }

    private string ResolveStatusMessage(MachineStatusSnapshot snapshot) =>
        snapshot.Session.ActivityState switch
        {
            MachineActivityState.Dispensing => T(nameof(AppLanguageStrings.DashboardStatusDispensing)),
            MachineActivityState.Cleaning => T(nameof(AppLanguageStrings.DashboardStatusCleaning)),
            _ when snapshot.Session.IsCardSelectionBlocked => T(nameof(AppLanguageStrings.DashboardStatusCashBlocked)),
            _ when SelectedPaymentMethod == PaymentMethod.Cash => T(nameof(AppLanguageStrings.DashboardStatusCashReady)),
            _ => T(nameof(AppLanguageStrings.DashboardStatusReady)),
        };

    private void RotateSlides()
    {
        if (PromoSlides.Count <= 1)
        {
            return;
        }

        _promoTick++;
        if (_promoTick % 6 == 0)
        {
            CurrentPromoIndex = (CurrentPromoIndex + 1) % PromoSlides.Count;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        SelectedLanguageOption = languageService.GetCurrentLanguageOption();
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(TankCapacityDisplay));
        OnPropertyChanged(nameof(SelectedTotalDisplay));
        OnPropertyChanged(nameof(ContactDisplay));

        StatusMessage = _lastSnapshot is null
            ? T(nameof(AppLanguageStrings.DashboardStatusStarting))
            : ResolveStatusMessage(_lastSnapshot);
    }

    private string T(string key) => languageService.GetText(key);
}
