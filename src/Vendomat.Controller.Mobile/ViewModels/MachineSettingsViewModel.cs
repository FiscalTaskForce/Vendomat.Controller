using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Client.Localization;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Security;
using Vendomat.Controller.Mobile.Models;
using Vendomat.Controller.Mobile.Services;

namespace Vendomat.Controller.Mobile.ViewModels;

public partial class MachineSettingsViewModel(
    PairedMachineStore pairedMachineStore,
    VendomatRemoteClient remoteClient,
    LanguageService languageService) : ObservableObject
{
    private static readonly TimeSpan RemoteRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RemoteApplyTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan RemoteApplyPollInterval = TimeSpan.FromMilliseconds(750);
    private const string SalesTabKey = "sales";
    private const string ConnectionTabKey = "connection";
    private const string GeneralTabKey = "general";
    private const string PaymentsTabKey = "payments";
    private const string ValidatorTabKey = "validator";
    private const string Esp32TabKey = "esp32";
    private const string CleaningTabKey = "cleaning";

    private Guid _machineId;
    private PairedMachineRecord? _record;
    private MachineSettings? _loadedSettings;
    private string _statusMessageKey = nameof(AppLanguageStrings.MobileSettingsStatusLoading);
    private CancellationTokenSource? _refreshLoopCts;
    private bool _isApplyingRemoteSettings;
    private bool _isSaving;
    private DateTime _lastLocalEditUtc = DateTime.MinValue;

    [ObservableProperty]
    private string selectedTab = ConnectionTabKey;

    [ObservableProperty]
    private MachineConnectionPreference selectedConnectionPreference = MachineConnectionPreference.Auto;

    [ObservableProperty]
    private string machineIdentifier = string.Empty;

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
    private string newAdminPasscode = string.Empty;

    [ObservableProperty]
    private string confirmAdminPasscode = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<CashChannelSetting> cashChannels = [];

    [ObservableProperty]
    private string activeConnectionModeTitle = string.Empty;

    [ObservableProperty]
    private string activeConnectionEndpoint = string.Empty;

    [ObservableProperty]
    private string connectionAvailabilityText = string.Empty;

    [ObservableProperty]
    private string connectionFallbackText = string.Empty;

    public ObservableCollection<ConnectionModeOptionViewModel> ConnectionModeOptions { get; } = [];

    public bool IsConnectionTabSelected => SelectedTab == ConnectionTabKey;

    public bool IsSalesTabSelected => SelectedTab == SalesTabKey;

    public bool IsGeneralTabSelected => SelectedTab == GeneralTabKey;

    public bool IsPaymentsTabSelected => SelectedTab == PaymentsTabKey;

    public bool IsValidatorTabSelected => SelectedTab == ValidatorTabKey;

    public bool IsEsp32TabSelected => SelectedTab == Esp32TabKey;

    public bool IsCleaningTabSelected => SelectedTab == CleaningTabKey;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_isApplyingRemoteSettings || string.IsNullOrWhiteSpace(e.PropertyName))
        {
            return;
        }

        if (e.PropertyName is nameof(StatusMessage)
            or nameof(SelectedConnectionPreference)
            or nameof(ActiveConnectionModeTitle)
            or nameof(ActiveConnectionEndpoint)
            or nameof(ConnectionAvailabilityText)
            or nameof(ConnectionFallbackText)
            or nameof(SelectedTab)
            or nameof(IsConnectionTabSelected)
            or nameof(IsSalesTabSelected)
            or nameof(IsGeneralTabSelected)
            or nameof(IsPaymentsTabSelected)
            or nameof(IsValidatorTabSelected)
            or nameof(IsEsp32TabSelected)
            or nameof(IsCleaningTabSelected))
        {
            return;
        }

        _lastLocalEditUtc = DateTime.UtcNow;
    }

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsConnectionTabSelected));
        OnPropertyChanged(nameof(IsSalesTabSelected));
        OnPropertyChanged(nameof(IsGeneralTabSelected));
        OnPropertyChanged(nameof(IsPaymentsTabSelected));
        OnPropertyChanged(nameof(IsValidatorTabSelected));
        OnPropertyChanged(nameof(IsEsp32TabSelected));
        OnPropertyChanged(nameof(IsCleaningTabSelected));
    }

    partial void OnSelectedConnectionPreferenceChanged(MachineConnectionPreference value)
    {
        SyncConnectionModeSelection();
        RefreshConnectionSummary();
        _ = PersistConnectionPreferenceAsync();
    }

    public async Task LoadAsync(Guid machineId)
    {
        languageService.LanguageChanged -= OnLanguageChanged;
        languageService.LanguageChanged += OnLanguageChanged;

        _refreshLoopCts?.Cancel();
        _refreshLoopCts = new CancellationTokenSource();
        _machineId = machineId;
        SetStatus(nameof(AppLanguageStrings.MobileSettingsStatusLoading));

        _record = await pairedMachineStore.GetAsync(machineId);
        if (_record is null)
        {
            SetStatus(nameof(AppLanguageStrings.MobileSettingsStatusLoadError));
            return;
        }

        SelectedConnectionPreference = _record.PreferredConnectionPreference;
        RebuildConnectionModeOptions();
        RefreshConnectionSummary();

        var loaded = await RefreshFromRemoteAsync(updateStatus: true);
        if (loaded)
        {
            _ = RunRefreshLoopAsync(_refreshLoopCts.Token);
        }
    }

    public void Stop()
    {
        _refreshLoopCts?.Cancel();
        _refreshLoopCts = null;
        languageService.LanguageChanged -= OnLanguageChanged;
    }

    [RelayCommand]
    private Task Save() => SaveCoreAsync();

    [RelayCommand]
    private void ShowConnectionTab() => SelectedTab = ConnectionTabKey;

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
    private Task RunContinuousCleaning() => RunCleaningAsync(SanitationMode.Continuous, TimeSpan.FromSeconds(20), TimeSpan.Zero, TimeSpan.Zero);

    [RelayCommand]
    private Task RunPulsedCleaning() => RunCleaningAsync(SanitationMode.Pulsed, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

    [RelayCommand]
    private void SelectConnectionMode(ConnectionModeOptionViewModel? option)
    {
        if (option is null)
        {
            return;
        }

        SelectedConnectionPreference = option.Preference;
    }

    private async Task<bool> RefreshFromRemoteAsync(bool updateStatus = false)
    {
        if (_record is null)
        {
            return false;
        }

        try
        {
            var result = await remoteClient.GetSettingsAsync(_record);
            var settings = result.Payload;
            _record.RememberSuccessfulConnection(result.ApiBaseUrl, result.ConnectionMode);
            _record.LocalApiBaseUrl = settings.LocalApiBaseUrl;
            _record.PublicApiBaseUrl = settings.PublicApiBaseUrl;
            _record.CloudApiBaseUrl = settings.CloudApiBaseUrl;
            if (!string.IsNullOrWhiteSpace(settings.CompanionAccessToken))
            {
                _record.CompanionAccessToken = settings.CompanionAccessToken;
            }
            _loadedSettings = settings;

            Apply(settings);
            await pairedMachineStore.AddOrUpdateAsync(_record);
            RefreshConnectionSummary();

            if (updateStatus)
            {
                SetStatus(nameof(AppLanguageStrings.MobileSettingsStatusLoaded));
            }

            return true;
        }
        catch
        {
            if (updateStatus)
            {
                SetStatus(nameof(AppLanguageStrings.MobileSettingsStatusLoadError));
            }

            return false;
        }
    }

    private async Task SaveCoreAsync()
    {
        if (_record is null)
        {
            return;
        }

        try
        {
            _isSaving = true;
            await PersistConnectionPreferenceAsync();
            SetStatus(nameof(AppLanguageStrings.MobileSettingsStatusSending));
            var settings = _loadedSettings is null ? new MachineSettings() : CloneSettings(_loadedSettings);
            settings.MachineId = _machineId;
            settings.MachineName = MachineName;
            settings.PricePerLiter = PricePerLiter;
            settings.PulsesPerLiter = PulsesPerLiter;
            settings.CurrentStockLiters = CurrentStockLiters;
            settings.TankCapacityLiters = TankCapacityLiters;
            settings.LowStockThresholdLiters = LowStockThresholdLiters;
            settings.CashPaymentEnabled = CashPaymentEnabled;
            settings.CardPaymentEnabled = CardPaymentEnabled;
            settings.BillValidatorEnabled = BillValidatorEnabled;
            settings.BillValidatorPortName = BillValidatorPortName;
            settings.BillValidatorBaudRate = BillValidatorBaudRate;
            settings.BillValidatorEscrowMode = BillValidatorEscrowMode;
            settings.Esp32Enabled = Esp32Enabled;
            settings.Esp32PortName = Esp32PortName;
            settings.Esp32BaudRate = Esp32BaudRate;
            settings.Esp32AutoDiscover = Esp32AutoDiscover;
            settings.ContactPhone = ContactPhone;
            settings.ContactEmail = ContactEmail;
            settings.LocalApiBaseUrl = LocalApiBaseUrl;
            settings.PublicApiBaseUrl = PublicApiBaseUrl;
            settings.CloudApiBaseUrl = CloudApiBaseUrl;
            settings.CashChannels = CashChannels
                .Select(CloneChannel)
                .ToList();

            if (!TryApplyAdminPasscode(settings))
            {
                return;
            }

            var saveResult = await remoteClient.SaveSettingsAsync(_record, settings);
            var savedSettings = saveResult.Payload;
            _loadedSettings = CloneSettings(savedSettings);
            UpdateRecordFromSettings(_record, saveResult, savedSettings);
            await pairedMachineStore.AddOrUpdateAsync(_record);
            RefreshConnectionSummary();

            SetStatus(nameof(AppLanguageStrings.MobileSettingsStatusAwaitingDevice));
            var appliedSettings = await WaitForAppliedSettingsAsync(_record, settings);
            if (appliedSettings is not null)
            {
                _loadedSettings = CloneSettings(appliedSettings);
                Apply(appliedSettings);
                UpdateRecordFromSettings(_record, _record.ApiBaseUrl, _record.LastConnectionMode, appliedSettings);
                await pairedMachineStore.AddOrUpdateAsync(_record);
                RefreshConnectionSummary();
            }

            NewAdminPasscode = string.Empty;
            ConfirmAdminPasscode = string.Empty;
            _lastLocalEditUtc = DateTime.MinValue;
            SetStatus(appliedSettings is null
                ? nameof(AppLanguageStrings.MobileSettingsStatusSaved)
                : nameof(AppLanguageStrings.MobileSettingsStatusApplied));
        }
        catch
        {
            SetStatus(nameof(AppLanguageStrings.MobileSettingsStatusSaveError));
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task RunCleaningAsync(SanitationMode mode, TimeSpan duration, TimeSpan pulseOn, TimeSpan pulseOff)
    {
        if (_record is null)
        {
            return;
        }

        try
        {
            var result = await remoteClient.RunSanitationAsync(_record, new SanitationRequest
            {
                Mode = mode,
                Duration = duration,
                PulseOn = pulseOn,
                PulseOff = pulseOff,
            });
            _record.RememberSuccessfulConnection(result.ApiBaseUrl, result.ConnectionMode);
            await pairedMachineStore.AddOrUpdateAsync(_record);
            RefreshConnectionSummary();

            await RefreshFromRemoteAsync(updateStatus: false);
            SetStatus(nameof(AppLanguageStrings.MobileSettingsStatusSaved));
        }
        catch
        {
            SetStatus(nameof(AppLanguageStrings.MobileSettingsStatusSaveError));
        }
    }

    private void Apply(MachineSettings settings)
    {
        _isApplyingRemoteSettings = true;
        try
        {
            MachineIdentifier = settings.MachineId.ToString("N").ToUpperInvariant();
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
            CashChannels = new ObservableCollection<CashChannelSetting>(settings.CashChannels.Select(CloneChannel));
            RefreshConnectionSummary();
        }
        finally
        {
            _isApplyingRemoteSettings = false;
        }
    }

    private bool TryApplyAdminPasscode(MachineSettings settings)
    {
        var newPasscode = NewAdminPasscode?.Trim() ?? string.Empty;
        var confirmPasscode = ConfirmAdminPasscode?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(newPasscode) && string.IsNullOrWhiteSpace(confirmPasscode))
        {
            return true;
        }

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

        settings.AdminPasscodeHash = AdminPasscodeHasher.Hash(newPasscode);
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

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(RemoteRefreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_isSaving || DateTime.UtcNow - _lastLocalEditUtc < TimeSpan.FromSeconds(8))
                {
                    continue;
                }

                await MainThread.InvokeOnMainThreadAsync(() => RefreshFromRemoteAsync(updateStatus: false));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        StatusMessage = T(_statusMessageKey);
        RebuildConnectionModeOptions();
        RefreshConnectionSummary();
    }

    private void RebuildConnectionModeOptions()
    {
        ConnectionModeOptions.Clear();
        ConnectionModeOptions.Add(new ConnectionModeOptionViewModel(
            MachineConnectionPreference.Auto,
            T(nameof(AppLanguageStrings.MobileConnectionModeAutoTitle)),
            T(nameof(AppLanguageStrings.MobileConnectionModeAutoDescription))));
        ConnectionModeOptions.Add(new ConnectionModeOptionViewModel(
            MachineConnectionPreference.LocalFirst,
            T(nameof(AppLanguageStrings.MobileConnectionModeLocalTitle)),
            T(nameof(AppLanguageStrings.MobileConnectionModeLocalDescription))));
        ConnectionModeOptions.Add(new ConnectionModeOptionViewModel(
            MachineConnectionPreference.DirectFirst,
            T(nameof(AppLanguageStrings.MobileConnectionModeDirectTitle)),
            T(nameof(AppLanguageStrings.MobileConnectionModeDirectDescription))));
        ConnectionModeOptions.Add(new ConnectionModeOptionViewModel(
            MachineConnectionPreference.CloudBridgeOnly,
            T(nameof(AppLanguageStrings.MobileConnectionModeBridgeTitle)),
            T(nameof(AppLanguageStrings.MobileConnectionModeBridgeDescription))));
        SyncConnectionModeSelection();
    }

    private void SyncConnectionModeSelection()
    {
        foreach (var option in ConnectionModeOptions)
        {
            option.IsSelected = option.Preference == SelectedConnectionPreference;
        }
    }

    private void RefreshConnectionSummary()
    {
        var localAvailable = ConnectionStrategyResolver.IsSameLocalNetwork(LocalApiBaseUrl);
        ConnectionAvailabilityText = localAvailable
            ? T(nameof(AppLanguageStrings.MobileConnectionLocalDetected))
            : T(nameof(AppLanguageStrings.MobileConnectionLocalNotDetected));
        ConnectionFallbackText = string.Join(
            " -> ",
            ConnectionStrategyResolver
                .GetAttemptOrder(SelectedConnectionPreference, localAvailable)
                .Select(GetConnectionModeTitle));

        var activeMode = _record?.LastConnectionMode ?? MachineConnectionMode.Unknown;
        if (activeMode == MachineConnectionMode.Unknown && _record is not null)
        {
            activeMode = ConnectionStrategyResolver.InferMode(_record, _record.ApiBaseUrl);
        }

        ActiveConnectionModeTitle = GetConnectionModeTitle(activeMode);
        ActiveConnectionEndpoint = !string.IsNullOrWhiteSpace(_record?.LastConnectionEndpoint)
            ? _record!.LastConnectionEndpoint
            : (!string.IsNullOrWhiteSpace(_record?.ApiBaseUrl)
                ? _record.ApiBaseUrl
                : T(nameof(AppLanguageStrings.MobileConnectionEndpointMissing)));
    }

    private async Task PersistConnectionPreferenceAsync()
    {
        if (_record is null)
        {
            return;
        }

        if (_record.PreferredConnectionPreference == SelectedConnectionPreference)
        {
            return;
        }

        _record.PreferredConnectionPreference = SelectedConnectionPreference;
        await pairedMachineStore.AddOrUpdateAsync(_record);
    }

    private string GetConnectionModeTitle(MachineConnectionMode mode) => mode switch
    {
        MachineConnectionMode.LocalNetwork => T(nameof(AppLanguageStrings.MobileConnectionActiveLocal)),
        MachineConnectionMode.DirectInternet => T(nameof(AppLanguageStrings.MobileConnectionActiveDirect)),
        MachineConnectionMode.CloudBridge => T(nameof(AppLanguageStrings.MobileConnectionActiveBridge)),
        _ => T(nameof(AppLanguageStrings.MobileConnectionActiveUnknown)),
    };

    private void SetStatus(string key)
    {
        _statusMessageKey = key;
        StatusMessage = T(key);
    }

    private string T(string key) => languageService.GetText(key);

    private async Task<MachineSettings?> WaitForAppliedSettingsAsync(PairedMachineRecord record, MachineSettings expectedSettings)
    {
        using var timeoutCts = new CancellationTokenSource(RemoteApplyTimeout);
        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RemoteApplyPollInterval, timeoutCts.Token);
                var result = await remoteClient.GetSettingsAsync(record, timeoutCts.Token);
                var remoteSettings = result.Payload;
                UpdateRecordFromSettings(record, result, remoteSettings);
                if (AreEquivalent(expectedSettings, remoteSettings))
                {
                    return remoteSettings;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // The bridge may still be syncing the applied values back from the tablet.
            }
        }

        return null;
    }

    private static bool AreEquivalent(MachineSettings expected, MachineSettings actual)
    {
        if (expected.MachineId != actual.MachineId
            || !string.Equals(expected.MachineName?.Trim(), actual.MachineName?.Trim(), StringComparison.Ordinal)
            || expected.PricePerLiter != actual.PricePerLiter
            || expected.PulsesPerLiter != actual.PulsesPerLiter
            || expected.CurrentStockLiters != actual.CurrentStockLiters
            || expected.TankCapacityLiters != actual.TankCapacityLiters
            || expected.LowStockThresholdLiters != actual.LowStockThresholdLiters
            || expected.CashPaymentEnabled != actual.CashPaymentEnabled
            || expected.CardPaymentEnabled != actual.CardPaymentEnabled
            || expected.BillValidatorEnabled != actual.BillValidatorEnabled
            || !string.Equals(expected.BillValidatorPortName?.Trim(), actual.BillValidatorPortName?.Trim(), StringComparison.Ordinal)
            || expected.BillValidatorBaudRate != actual.BillValidatorBaudRate
            || expected.BillValidatorEscrowMode != actual.BillValidatorEscrowMode
            || expected.Esp32Enabled != actual.Esp32Enabled
            || !string.Equals(expected.Esp32PortName?.Trim(), actual.Esp32PortName?.Trim(), StringComparison.Ordinal)
            || expected.Esp32BaudRate != actual.Esp32BaudRate
            || expected.Esp32AutoDiscover != actual.Esp32AutoDiscover
            || !string.Equals(expected.ContactPhone?.Trim(), actual.ContactPhone?.Trim(), StringComparison.Ordinal)
            || !string.Equals(expected.ContactEmail?.Trim(), actual.ContactEmail?.Trim(), StringComparison.Ordinal)
            || !string.Equals(NormalizeApiBaseUrl(expected.LocalApiBaseUrl, string.Empty), NormalizeApiBaseUrl(actual.LocalApiBaseUrl, string.Empty), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeApiBaseUrl(expected.PublicApiBaseUrl, string.Empty), NormalizeApiBaseUrl(actual.PublicApiBaseUrl, string.Empty), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeApiBaseUrl(expected.CloudApiBaseUrl, string.Empty), NormalizeApiBaseUrl(actual.CloudApiBaseUrl, string.Empty), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.AdminPasscodeHash?.Trim(), actual.AdminPasscodeHash?.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var expectedChannels = expected.CashChannels.OrderBy(channel => channel.Channel).ToList();
        var actualChannels = actual.CashChannels.OrderBy(channel => channel.Channel).ToList();
        if (expectedChannels.Count != actualChannels.Count)
        {
            return false;
        }

        for (var index = 0; index < expectedChannels.Count; index++)
        {
            var expectedChannel = expectedChannels[index];
            var actualChannel = actualChannels[index];
            if (expectedChannel.Channel != actualChannel.Channel
                || !string.Equals(expectedChannel.Label?.Trim(), actualChannel.Label?.Trim(), StringComparison.Ordinal)
                || expectedChannel.Amount != actualChannel.Amount
                || expectedChannel.IsEnabled != actualChannel.IsEnabled)
            {
                return false;
            }
        }

        return true;
    }

    private static void UpdateRecordFromSettings(
        PairedMachineRecord record,
        RemoteCallResult<MachineSettings> result,
        MachineSettings settings) =>
        UpdateRecordFromSettings(record, result.ApiBaseUrl, result.ConnectionMode, settings);

    private static void UpdateRecordFromSettings(
        PairedMachineRecord record,
        string? apiBaseUrl,
        MachineConnectionMode connectionMode,
        MachineSettings settings)
    {
        record.RememberSuccessfulConnection(apiBaseUrl, connectionMode);

        record.MachineName = settings.MachineName;
        record.LocalApiBaseUrl = settings.LocalApiBaseUrl;
        record.PublicApiBaseUrl = settings.PublicApiBaseUrl;
        record.CloudApiBaseUrl = settings.CloudApiBaseUrl;
        if (!string.IsNullOrWhiteSpace(settings.CompanionAccessToken))
        {
            record.CompanionAccessToken = settings.CompanionAccessToken;
        }
    }

    private static string NormalizeApiBaseUrl(string? value, string fallback)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized;
    }

    private static CashChannelSetting CloneChannel(CashChannelSetting channel) => new()
    {
        Channel = channel.Channel,
        Label = channel.Label,
        Amount = channel.Amount,
        IsEnabled = channel.IsEnabled,
    };

    private static MachineSettings CloneSettings(MachineSettings settings) => new()
    {
        MachineId = settings.MachineId,
        MachineName = settings.MachineName,
        PricePerLiter = settings.PricePerLiter,
        PulsesPerLiter = settings.PulsesPerLiter,
        CurrentStockLiters = settings.CurrentStockLiters,
        TankCapacityLiters = settings.TankCapacityLiters,
        LowStockThresholdLiters = settings.LowStockThresholdLiters,
        CashPaymentEnabled = settings.CashPaymentEnabled,
        CardPaymentEnabled = settings.CardPaymentEnabled,
        BillValidatorEnabled = settings.BillValidatorEnabled,
        BillValidatorPortName = settings.BillValidatorPortName,
        BillValidatorBaudRate = settings.BillValidatorBaudRate,
        BillValidatorEscrowMode = settings.BillValidatorEscrowMode,
        Esp32Enabled = settings.Esp32Enabled,
        Esp32PortName = settings.Esp32PortName,
        Esp32BaudRate = settings.Esp32BaudRate,
        Esp32AutoDiscover = settings.Esp32AutoDiscover,
        RuntimeMode = settings.RuntimeMode,
        ContactPhone = settings.ContactPhone,
        ContactEmail = settings.ContactEmail,
        LocalApiBaseUrl = settings.LocalApiBaseUrl,
        PublicApiBaseUrl = settings.PublicApiBaseUrl,
        CloudApiBaseUrl = settings.CloudApiBaseUrl,
        CloudMachineToken = settings.CloudMachineToken,
        CompanionAccessToken = settings.CompanionAccessToken,
        AdminPasscodeHash = settings.AdminPasscodeHash,
        PromoRotationInterval = settings.PromoRotationInterval,
        CashChannels = settings.CashChannels.Select(CloneChannel).ToList(),
    };
}
