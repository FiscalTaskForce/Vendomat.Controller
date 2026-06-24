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
    LanguageService languageService,
    AdminPasscodeRateLimiter adminPasscodeRateLimiter) : ObservableObject
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
    private bool isCardPaymentVisible;

    [ObservableProperty]
    private bool isPaymentSelectorVisible;

    [ObservableProperty]
    private bool isDispensing;

    [ObservableProperty]
    private bool isRemoteOperationOverlayVisible;

    [ObservableProperty]
    private bool canStopRemoteOperation;

    [ObservableProperty]
    private string remoteOperationTitle = string.Empty;

    [ObservableProperty]
    private string remoteOperationMessage = string.Empty;

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
    private PromoSlide? currentPromoSlide;

    [ObservableProperty]
    private LanguageOption? selectedLanguageOption;

    public IReadOnlyList<LanguageOption> AvailableLanguages => languageService.AvailableLanguages;

    public decimal DispenseProgress => RequestedLiters <= 0 ? 0 : Math.Clamp(DispensedLiters / RequestedLiters, 0m, 1m);

    public decimal RemainingLiters => Math.Max(0, Math.Round(RequestedLiters - DispensedLiters, 2));

    public decimal RequestedLitersDisplay => IsDispensing ? RemainingLiters : RequestedLiters;

    public string RequestedLitersTitle => IsDispensing
        ? T(nameof(AppLanguageStrings.DashboardRemainingLitersTitle))
        : T(nameof(AppLanguageStrings.DashboardRequestedLitersTitle));

    public bool CanAdjustLiters => SelectedPaymentMethod == PaymentMethod.Card && !IsDispensing && CanUseCard;

    public bool CanStartDispense => IsDispensing || RequestedLiters > 0;

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

    /// <summary>Seconds remaining on the admin lockout after the last verify attempt, or 0.</summary>
    public int AdminUnlockRetrySeconds { get; private set; }

    public async Task<bool> VerifyAdminPasscodeAsync(string passcode)
    {
        var settings = await machineRuntimeService.GetSettingsAsync();
        var accepted = adminPasscodeRateLimiter.TryVerify(settings.AdminPasscodeHash, passcode);
        AdminUnlockRetrySeconds = (int)Math.Ceiling(adminPasscodeRateLimiter.RetryAfter.TotalSeconds);
        return accepted;
    }

    public async Task<bool> IsDefaultAdminPasscodeActiveAsync()
    {
        var settings = await machineRuntimeService.GetSettingsAsync();
        return AdminPasscodeHasher.IsDefaultHash(settings.AdminPasscodeHash);
    }

    public async Task ChangeAdminPasscodeAsync(string passcode)
    {
        var settings = await machineRuntimeService.GetSettingsAsync();
        settings.AdminPasscodeHash = AdminPasscodeHasher.Hash(passcode);
        await machineRuntimeService.SaveSettingsAsync(settings);
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

    partial void OnCurrentPromoIndexChanged(int value) => UpdateCurrentPromoSlide();

    partial void OnSelectedPaymentMethodChanged(PaymentMethod value) => OnPropertyChanged(nameof(CanAdjustLiters));

    partial void OnCanUseCardChanged(bool value) => OnPropertyChanged(nameof(CanAdjustLiters));

    partial void OnRequestedLitersChanged(decimal value)
    {
        OnPropertyChanged(nameof(CanStartDispense));
        OnPropertyChanged(nameof(DispenseProgress));
        OnPropertyChanged(nameof(RemainingLiters));
        OnPropertyChanged(nameof(RequestedLitersDisplay));
        OnPropertyChanged(nameof(SelectedTotalDisplay));
    }

    partial void OnDispensedLitersChanged(decimal value)
    {
        OnPropertyChanged(nameof(DispenseProgress));
        OnPropertyChanged(nameof(RemainingLiters));
        OnPropertyChanged(nameof(RequestedLitersDisplay));
    }

    partial void OnTotalAmountChanged(decimal value) => OnPropertyChanged(nameof(SelectedTotalDisplay));

    partial void OnTankCapacityLitersChanged(decimal value) => OnPropertyChanged(nameof(TankCapacityDisplay));

    partial void OnContactValueChanged(string value) => OnPropertyChanged(nameof(ContactDisplay));

    partial void OnIsDispensingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAdjustLiters));
        OnPropertyChanged(nameof(CanStartDispense));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(RequestedLitersTitle));
        OnPropertyChanged(nameof(RequestedLitersDisplay));
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
            if (IsDispensing)
            {
                await machineRuntimeService.StopDispenseAsync();
                StatusMessage = T(nameof(AppLanguageStrings.DashboardStatusReady));
                await LoadStatusAsync();
                return;
            }

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

    [RelayCommand]
    private async Task StopRemoteOperation()
    {
        try
        {
            if (_lastSnapshot?.Session.ActivityState == MachineActivityState.Cleaning)
            {
                await machineRuntimeService.StopSanitationAsync();
                await LoadStatusAsync();
            }
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
        CurrentPromoIndex = 0;
        UpdateCurrentPromoSlide();
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
        IsRemoteOperationOverlayVisible = snapshot.Session.ActivityState is MachineActivityState.Cleaning or MachineActivityState.Priming;
        CanStopRemoteOperation = snapshot.Session.ActivityState == MachineActivityState.Cleaning;
        RemoteOperationTitle = snapshot.Session.IsRemoteOperation
            ? T(nameof(AppLanguageStrings.DashboardRemoteOperationTitle))
            : T(nameof(AppLanguageStrings.SettingsOperationPopupTitle));
        RemoteOperationMessage = string.IsNullOrWhiteSpace(snapshot.Session.OperationMessage)
            ? T(nameof(AppLanguageStrings.DashboardRemoteOperationMessage))
            : snapshot.Session.OperationMessage;

        var isMachineBusy = snapshot.Session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning or MachineActivityState.Priming;
        CanUseCash = snapshot.Settings.CashPaymentEnabled && !isMachineBusy;
        IsCardPaymentVisible = snapshot.Settings.CardPaymentEnabled;
        IsPaymentSelectorVisible = snapshot.Settings.CashPaymentEnabled && snapshot.Settings.CardPaymentEnabled;
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
        OnPropertyChanged(nameof(RequestedLitersTitle));
        OnPropertyChanged(nameof(RequestedLitersDisplay));
        OnPropertyChanged(nameof(RemainingLiters));
    }

    private string ResolveStatusMessage(MachineStatusSnapshot snapshot) =>
        snapshot.Session.ActivityState switch
        {
            MachineActivityState.Dispensing => T(nameof(AppLanguageStrings.DashboardStatusDispensing)),
            MachineActivityState.Cleaning => T(nameof(AppLanguageStrings.DashboardStatusCleaning)),
            MachineActivityState.Priming => T(nameof(AppLanguageStrings.DashboardStatusPriming)),
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

    private void UpdateCurrentPromoSlide()
    {
        if (PromoSlides.Count == 0)
        {
            CurrentPromoSlide = null;
            return;
        }

        var normalizedIndex = Math.Clamp(CurrentPromoIndex, 0, PromoSlides.Count - 1);
        if (normalizedIndex != CurrentPromoIndex)
        {
            CurrentPromoIndex = normalizedIndex;
            return;
        }

        CurrentPromoSlide = PromoSlides[normalizedIndex];
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        SelectedLanguageOption = languageService.GetCurrentLanguageOption();
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(TankCapacityDisplay));
        OnPropertyChanged(nameof(SelectedTotalDisplay));
        OnPropertyChanged(nameof(ContactDisplay));
        OnPropertyChanged(nameof(RequestedLitersTitle));

        StatusMessage = _lastSnapshot is null
            ? T(nameof(AppLanguageStrings.DashboardStatusStarting))
            : ResolveStatusMessage(_lastSnapshot);
    }

    private string T(string key) => languageService.GetText(key);
}
