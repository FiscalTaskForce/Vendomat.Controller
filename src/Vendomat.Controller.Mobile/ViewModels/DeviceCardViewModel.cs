using CommunityToolkit.Mvvm.ComponentModel;
using Vendomat.Controller.Client.Localization;
using Vendomat.Controller.Mobile.Models;
using Vendomat.Controller.Mobile.Services;

namespace Vendomat.Controller.Mobile.ViewModels;

public partial class DeviceCardViewModel(LanguageService languageService) : ObservableObject
{
    [ObservableProperty]
    private Guid machineId;

    [ObservableProperty]
    private string machineName = string.Empty;

    [ObservableProperty]
    private string apiBaseUrl = string.Empty;

    [ObservableProperty]
    private bool isOnline;

    [ObservableProperty]
    private DateTimeOffset? lastSeenUtc;

    [ObservableProperty]
    private decimal lastKnownStockLiters;

    [ObservableProperty]
    private float lastKnownTemperatureCelsius;

    [ObservableProperty]
    private decimal lastKnownPricePerLiter;

    [ObservableProperty]
    private MachineConnectionMode lastConnectionMode;

    public string ConnectionModeText => LastConnectionMode switch
    {
        MachineConnectionMode.LocalNetwork => languageService.GetText(nameof(AppLanguageStrings.MobileConnectionActiveLocal)),
        MachineConnectionMode.DirectInternet => languageService.GetText(nameof(AppLanguageStrings.MobileConnectionActiveDirect)),
        MachineConnectionMode.CloudBridge => languageService.GetText(nameof(AppLanguageStrings.MobileConnectionActiveBridge)),
        _ => languageService.GetText(nameof(AppLanguageStrings.MobileConnectionActiveUnknown)),
    };

    public string AvailabilityText => IsOnline
        ? languageService.GetText(nameof(AppLanguageStrings.MobileDevicesStatusOnline))
        : languageService.GetText(nameof(AppLanguageStrings.MobileDevicesStatusOffline));

    public string LastSeenText => LastSeenUtc is null
        ? AvailabilityText
        : string.Format(
            languageService.GetText(nameof(AppLanguageStrings.MobileDevicesLastSeenFormat)),
            LastSeenUtc.Value.LocalDateTime);

    public string SnapshotSummary => $"{StockText} | {TemperatureText} | {PriceText}";

    public string StockLabel => $"{languageService.GetText(nameof(AppLanguageStrings.MobileDetailStockTitle))}:";

    public string StockText => $"{LastKnownStockLiters:0.##} L";

    public string TemperatureLabel => $"{languageService.GetText(nameof(AppLanguageStrings.MobileDetailTemperatureTitle))}:";

    public string TemperatureText => $"{LastKnownTemperatureCelsius:0.0} ℃";

    public string PriceLabel => $"{languageService.GetText(nameof(AppLanguageStrings.MobileDetailPriceTitle))}:";

    public string PriceText => $"{LastKnownPricePerLiter:0.00} RON";

    partial void OnIsOnlineChanged(bool value)
    {
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(LastSeenText));
    }

    partial void OnLastSeenUtcChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(LastSeenText));

    partial void OnLastKnownStockLitersChanged(decimal value)
    {
        OnPropertyChanged(nameof(SnapshotSummary));
        OnPropertyChanged(nameof(StockText));
    }

    partial void OnLastKnownTemperatureCelsiusChanged(float value)
    {
        OnPropertyChanged(nameof(SnapshotSummary));
        OnPropertyChanged(nameof(TemperatureText));
    }

    partial void OnLastKnownPricePerLiterChanged(decimal value)
    {
        OnPropertyChanged(nameof(SnapshotSummary));
        OnPropertyChanged(nameof(PriceText));
    }

    partial void OnLastConnectionModeChanged(MachineConnectionMode value) => OnPropertyChanged(nameof(ConnectionModeText));

    public void Apply(PairedMachineRecord record)
    {
        MachineId = record.MachineId;
        MachineName = record.MachineName;
        ApiBaseUrl = ConnectionStrategyResolver.GetDisplayEndpoint(record);
        IsOnline = record.LastSeenOnline;
        LastSeenUtc = record.LastSeenUtc;
        LastKnownStockLiters = record.LastKnownStockLiters;
        LastKnownTemperatureCelsius = record.LastKnownTemperatureCelsius;
        LastKnownPricePerLiter = record.LastKnownPricePerLiter;
        LastConnectionMode = ConnectionStrategyResolver.InferActiveMode(record);
    }

    public void RefreshLocalized()
    {
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(LastSeenText));
        OnPropertyChanged(nameof(ConnectionModeText));
        OnPropertyChanged(nameof(StockLabel));
        OnPropertyChanged(nameof(TemperatureLabel));
        OnPropertyChanged(nameof(PriceLabel));
    }
}
