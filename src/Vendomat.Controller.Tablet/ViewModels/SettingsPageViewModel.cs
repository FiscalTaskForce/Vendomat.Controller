using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using QRCoder;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Client.Localization;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Security;
using Vendomat.Controller.Tablet.Services;

namespace Vendomat.Controller.Tablet.ViewModels;

public partial class SettingsPageViewModel(
    IMachineRuntimeService machineRuntimeService,
    CloudBridgeService cloudBridgeService,
    LanguageService languageService) : ObservableObject
{
    private const string SalesTabKey = "sales";
    private const string GeneralTabKey = "general";
    private const string PaymentsTabKey = "payments";
    private const string ValidatorTabKey = "validator";
    private const string Esp32TabKey = "esp32";
    private const string CleaningTabKey = "cleaning";
    private string _statusMessageKey = nameof(AppLanguageStrings.SettingsLoadingStatus);

    [ObservableProperty]
    private string selectedTab = SalesTabKey;

    [ObservableProperty]
    private string machineName = string.Empty;

    [ObservableProperty]
    private decimal pricePerLiter;

    [ObservableProperty]
    private int pulsesPerLiter;

    [ObservableProperty]
    private decimal currentStockLiters;

    [ObservableProperty]
    private decimal tankCapacityLiters;

    [ObservableProperty]
    private decimal lowStockThresholdLiters;

    [ObservableProperty]
    private bool cashPaymentEnabled;

    [ObservableProperty]
    private bool cardPaymentEnabled;

    [ObservableProperty]
    private bool billValidatorEnabled;

    [ObservableProperty]
    private string billValidatorPortName = string.Empty;

    [ObservableProperty]
    private int billValidatorBaudRate;

    [ObservableProperty]
    private bool billValidatorEscrowMode;

    [ObservableProperty]
    private bool esp32Enabled;

    [ObservableProperty]
    private string esp32PortName = string.Empty;

    [ObservableProperty]
    private int esp32BaudRate;

    [ObservableProperty]
    private bool esp32AutoDiscover;

    [ObservableProperty]
    private string contactPhone = string.Empty;

    [ObservableProperty]
    private string contactEmail = string.Empty;

    [ObservableProperty]
    private string localApiBaseUrl = string.Empty;

    [ObservableProperty]
    private string publicApiBaseUrl = string.Empty;

    [ObservableProperty]
    private string cloudApiBaseUrl = string.Empty;

    [ObservableProperty]
    private string machineIdentifier = string.Empty;

    [ObservableProperty]
    private string newAdminPasscode = string.Empty;

    [ObservableProperty]
    private string confirmAdminPasscode = string.Empty;

    [ObservableProperty]
    private string pairingCode = string.Empty;

    [ObservableProperty]
    private string pairingEndpoint = string.Empty;

    [ObservableProperty]
    private ImageSource? pairingQrImage;

    [ObservableProperty]
    private bool isPairingPopupVisible;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CashChannelSetting> cashChannels = [];

    public bool IsSalesTabSelected => SelectedTab == SalesTabKey;

    public bool IsGeneralTabSelected => SelectedTab == GeneralTabKey;

    public bool IsPaymentsTabSelected => SelectedTab == PaymentsTabKey;

    public bool IsValidatorTabSelected => SelectedTab == ValidatorTabKey;

    public bool IsEsp32TabSelected => SelectedTab == Esp32TabKey;

    public bool IsCleaningTabSelected => SelectedTab == CleaningTabKey;

    public bool HasPairingQr => PairingQrImage is not null;

    public bool CanShowPairingPopup => HasPairingQr;

    public string PairingCodeDisplay => string.IsNullOrWhiteSpace(PairingCode)
        ? string.Empty
        : string.Format(T(nameof(AppLanguageStrings.SettingsPairingCodeFormat)), PairingCode);

    public string PairingEndpointDisplay => string.IsNullOrWhiteSpace(PairingEndpoint)
        ? string.Empty
        : string.Format(T(nameof(AppLanguageStrings.SettingsPairingEndpointFormat)), PairingEndpoint);

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsSalesTabSelected));
        OnPropertyChanged(nameof(IsGeneralTabSelected));
        OnPropertyChanged(nameof(IsPaymentsTabSelected));
        OnPropertyChanged(nameof(IsValidatorTabSelected));
        OnPropertyChanged(nameof(IsEsp32TabSelected));
        OnPropertyChanged(nameof(IsCleaningTabSelected));
    }

    public async Task LoadAsync()
    {
        languageService.LanguageChanged += OnLanguageChanged;
        SetStatus(nameof(AppLanguageStrings.SettingsLoadingStatus));

        var settings = await machineRuntimeService.GetSettingsAsync();

        MachineName = settings.MachineName;
        PricePerLiter = settings.PricePerLiter;
        PulsesPerLiter = settings.PulsesPerLiter;
        CurrentStockLiters = settings.CurrentStockLiters;
        TankCapacityLiters = settings.TankCapacityLiters;
        LowStockThresholdLiters = settings.LowStockThresholdLiters;
        CashPaymentEnabled = settings.CashPaymentEnabled;
        CardPaymentEnabled = settings.CardPaymentEnabled;
        BillValidatorEnabled = settings.BillValidatorEnabled;
        BillValidatorPortName = settings.BillValidatorPortName;
        BillValidatorBaudRate = settings.BillValidatorBaudRate;
        BillValidatorEscrowMode = settings.BillValidatorEscrowMode;
        Esp32Enabled = settings.Esp32Enabled;
        Esp32PortName = settings.Esp32PortName;
        Esp32BaudRate = settings.Esp32BaudRate;
        Esp32AutoDiscover = settings.Esp32AutoDiscover;
        ContactPhone = settings.ContactPhone;
        ContactEmail = settings.ContactEmail;
        LocalApiBaseUrl = settings.LocalApiBaseUrl;
        PublicApiBaseUrl = settings.PublicApiBaseUrl;
        CloudApiBaseUrl = settings.CloudApiBaseUrl;
        MachineIdentifier = settings.MachineId.ToString("N").ToUpperInvariant();
        CashChannels = new ObservableCollection<CashChannelSetting>(
            settings.CashChannels.Select(x => new CashChannelSetting
            {
                Channel = x.Channel,
                Label = x.Label,
                Amount = x.Amount,
                IsEnabled = x.IsEnabled,
            }));

        SetStatus(nameof(AppLanguageStrings.SettingsReadyStatus));
    }

    public void Stop() => languageService.LanguageChanged -= OnLanguageChanged;

    partial void OnPairingQrImageChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasPairingQr));
        OnPropertyChanged(nameof(CanShowPairingPopup));
    }

    partial void OnPairingCodeChanged(string value) => OnPropertyChanged(nameof(PairingCodeDisplay));

    partial void OnPairingEndpointChanged(string value) => OnPropertyChanged(nameof(PairingEndpointDisplay));

    [RelayCommand]
    private async Task SaveSettings()
    {
        var existing = await machineRuntimeService.GetSettingsAsync();
        var shouldUpdateAdminPasscode = !string.IsNullOrWhiteSpace(NewAdminPasscode)
            || !string.IsNullOrWhiteSpace(ConfirmAdminPasscode);

        if (shouldUpdateAdminPasscode)
        {
            if (!TryBuildAdminPasscodeHash(out var adminPasscodeHash))
            {
                return;
            }

            existing.AdminPasscodeHash = adminPasscodeHash;
        }

        existing.MachineName = MachineName;
        existing.PricePerLiter = PricePerLiter;
        existing.PulsesPerLiter = PulsesPerLiter;
        existing.CurrentStockLiters = CurrentStockLiters;
        existing.TankCapacityLiters = TankCapacityLiters;
        existing.LowStockThresholdLiters = LowStockThresholdLiters;
        existing.CashPaymentEnabled = CashPaymentEnabled;
        existing.CardPaymentEnabled = CardPaymentEnabled;
        existing.BillValidatorEnabled = BillValidatorEnabled;
        existing.BillValidatorPortName = string.IsNullOrWhiteSpace(BillValidatorPortName) ? "/dev/ttyACM0" : BillValidatorPortName.Trim();
        existing.BillValidatorBaudRate = BillValidatorBaudRate <= 0 ? 115200 : BillValidatorBaudRate;
        existing.BillValidatorEscrowMode = BillValidatorEscrowMode;
        existing.Esp32Enabled = Esp32Enabled;
        existing.Esp32PortName = string.IsNullOrWhiteSpace(Esp32PortName) ? "/dev/ttyS3" : Esp32PortName.Trim();
        existing.Esp32BaudRate = Esp32BaudRate <= 0 ? 115200 : Esp32BaudRate;
        existing.Esp32AutoDiscover = Esp32AutoDiscover;
        existing.ContactPhone = ContactPhone;
        existing.ContactEmail = ContactEmail;
        existing.LocalApiBaseUrl = NormalizeApiBaseUrl(LocalApiBaseUrl, "http://vendomat.local:1326");
        existing.PublicApiBaseUrl = NormalizeApiBaseUrl(PublicApiBaseUrl, string.Empty);
        existing.CloudApiBaseUrl = NormalizeApiBaseUrl(CloudApiBaseUrl, string.Empty);
        existing.CashChannels = CashChannels.ToList();

        await machineRuntimeService.SaveSettingsAsync(existing);
        NewAdminPasscode = string.Empty;
        ConfirmAdminPasscode = string.Empty;
        SetStatus(shouldUpdateAdminPasscode
            ? nameof(AppLanguageStrings.SettingsAdminPasscodeUpdatedStatus)
            : nameof(AppLanguageStrings.SettingsSavedStatus));
    }

    [RelayCommand]
    private void ShowSalesTab() => SelectedTab = SalesTabKey;

    [RelayCommand]
    private void ShowGeneralTab() => SelectedTab = GeneralTabKey;

    [RelayCommand]
    private void ShowPaymentsTab() => SelectedTab = PaymentsTabKey;

    [RelayCommand]
    private void ShowValidatorTab() => SelectedTab = ValidatorTabKey;

    [RelayCommand]
    private void ShowEsp32Tab() => SelectedTab = Esp32TabKey;

    [RelayCommand]
    private void ShowCleaningTab() => SelectedTab = CleaningTabKey;

    [RelayCommand]
    private void IncrementNumericSetting(string settingName) => AdjustNumericSetting(settingName, +1);

    [RelayCommand]
    private void DecrementNumericSetting(string settingName) => AdjustNumericSetting(settingName, -1);

    [RelayCommand]
    private async Task GeneratePairingCode()
    {
        var settings = await machineRuntimeService.GetSettingsAsync();
        var normalizedLocalApiBaseUrl = NormalizeApiBaseUrl(LocalApiBaseUrl, "http://vendomat.local:1326");
        var normalizedPublicApiBaseUrl = NormalizeApiBaseUrl(PublicApiBaseUrl, string.Empty);
        var normalizedCloudApiBaseUrl = NormalizeApiBaseUrl(CloudApiBaseUrl, string.Empty);
        var shouldPersistPairingValues =
            !string.Equals(settings.MachineName, MachineName, StringComparison.Ordinal)
            || !string.Equals(settings.LocalApiBaseUrl, normalizedLocalApiBaseUrl, StringComparison.Ordinal)
            || !string.Equals(settings.PublicApiBaseUrl, normalizedPublicApiBaseUrl, StringComparison.Ordinal)
            || !string.Equals(settings.CloudApiBaseUrl, normalizedCloudApiBaseUrl, StringComparison.Ordinal);

        if (shouldPersistPairingValues)
        {
            settings.MachineName = MachineName;
            settings.LocalApiBaseUrl = normalizedLocalApiBaseUrl;
            settings.PublicApiBaseUrl = normalizedPublicApiBaseUrl;
            settings.CloudApiBaseUrl = normalizedCloudApiBaseUrl;
            await machineRuntimeService.SaveSettingsAsync(settings);
        }

        var payload = await machineRuntimeService.GeneratePairingAsync();
        if (!string.IsNullOrWhiteSpace(normalizedCloudApiBaseUrl))
        {
            payload.CloudApiBaseUrl = normalizedCloudApiBaseUrl;
            await cloudBridgeService.PublishPairingAsync(payload);
        }

        var payloadJson = payload.ToQrPayloadJson();

        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(payloadJson, QRCodeGenerator.ECCLevel.M);
        var qrCode = new PngByteQRCode(qrData);
        var bytes = qrCode.GetGraphic(24);

        PairingQrImage = ImageSource.FromStream(() => new MemoryStream(bytes));
        MachineIdentifier = payload.MachineId.ToString("N").ToUpperInvariant();
        PairingCode = payload.PairingCode;
        PairingEndpoint = !string.IsNullOrWhiteSpace(payload.CloudApiBaseUrl)
            ? payload.CloudApiBaseUrl
            : string.IsNullOrWhiteSpace(payload.PublicApiBaseUrl)
                ? payload.LocalApiBaseUrl
                : payload.PublicApiBaseUrl;
        IsPairingPopupVisible = true;
        SetStatus(nameof(AppLanguageStrings.SettingsQrReadyStatus));
    }

    [RelayCommand]
    private void ClosePairingPopup() => IsPairingPopupVisible = false;

    [RelayCommand]
    private async Task RunContinuousCleaning()
    {
        await machineRuntimeService.RunSanitationAsync(new SanitationRequest
        {
            Mode = SanitationMode.Continuous,
            Duration = TimeSpan.FromSeconds(20),
            PulseOn = TimeSpan.Zero,
            PulseOff = TimeSpan.Zero,
        });

        SetStatus(nameof(AppLanguageStrings.SettingsContinuousCleaningStatus));
    }

    [RelayCommand]
    private async Task RunPulsedCleaning()
    {
        await machineRuntimeService.RunSanitationAsync(new SanitationRequest
        {
            Mode = SanitationMode.Pulsed,
            Duration = TimeSpan.FromSeconds(15),
            PulseOn = TimeSpan.FromMilliseconds(500),
            PulseOff = TimeSpan.FromMilliseconds(500),
        });

        SetStatus(nameof(AppLanguageStrings.SettingsPulsedCleaningStatus));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        StatusMessage = T(_statusMessageKey);
        OnPropertyChanged(nameof(PairingCodeDisplay));
        OnPropertyChanged(nameof(PairingEndpointDisplay));
    }

    private void SetStatus(string key)
    {
        _statusMessageKey = key;
        StatusMessage = T(key);
    }

    private bool TryBuildAdminPasscodeHash(out string hash)
    {
        hash = string.Empty;

        var newPasscode = NewAdminPasscode?.Trim() ?? string.Empty;
        var confirmPasscode = ConfirmAdminPasscode?.Trim() ?? string.Empty;

        if (newPasscode.Length < 4)
        {
            SetStatus(nameof(AppLanguageStrings.SettingsAdminPasscodeTooShortStatus));
            return false;
        }

        if (!newPasscode.All(char.IsDigit) || !confirmPasscode.All(char.IsDigit))
        {
            SetStatus(nameof(AppLanguageStrings.SettingsAdminPasscodeDigitsOnlyStatus));
            return false;
        }

        if (!string.Equals(newPasscode, confirmPasscode, StringComparison.Ordinal))
        {
            SetStatus(nameof(AppLanguageStrings.SettingsAdminPasscodeMismatchStatus));
            return false;
        }

        hash = AdminPasscodeHasher.Hash(newPasscode);
        return true;
    }

    private void AdjustNumericSetting(string? settingName, int direction)
    {
        if (string.IsNullOrWhiteSpace(settingName) || direction == 0)
        {
            return;
        }

        switch (settingName)
        {
            case nameof(PricePerLiter):
                PricePerLiter = decimal.Round(Math.Max(0m, PricePerLiter + (0.5m * direction)), 2);
                break;

            case nameof(PulsesPerLiter):
                PulsesPerLiter = Math.Max(0, PulsesPerLiter + direction);
                break;

            case nameof(CurrentStockLiters):
                CurrentStockLiters = decimal.Round(Math.Max(0m, CurrentStockLiters + direction), 2);
                if (TankCapacityLiters > 0 && CurrentStockLiters > TankCapacityLiters)
                {
                    CurrentStockLiters = TankCapacityLiters;
                }
                break;

            case nameof(TankCapacityLiters):
                TankCapacityLiters = decimal.Round(Math.Max(0m, TankCapacityLiters + direction), 2);
                if (CurrentStockLiters > TankCapacityLiters)
                {
                    CurrentStockLiters = TankCapacityLiters;
                }

                if (LowStockThresholdLiters > TankCapacityLiters)
                {
                    LowStockThresholdLiters = TankCapacityLiters;
                }
                break;

            case nameof(LowStockThresholdLiters):
                var nextThreshold = decimal.Round(Math.Max(0m, LowStockThresholdLiters + direction), 2);
                if (TankCapacityLiters > 0)
                {
                    nextThreshold = Math.Min(nextThreshold, TankCapacityLiters);
                }

                LowStockThresholdLiters = nextThreshold;
                break;
        }
    }

    private static string NormalizeApiBaseUrl(string? value, string fallback)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized;
    }

    private string T(string key) => languageService.GetText(key);
}
