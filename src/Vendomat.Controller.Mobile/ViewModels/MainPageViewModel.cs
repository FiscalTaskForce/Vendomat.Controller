using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vendomat.Controller.Client.Localization;
using Vendomat.Controller.Mobile.Models;
using Vendomat.Controller.Mobile.Services;

namespace Vendomat.Controller.Mobile.ViewModels;

public partial class MainPageViewModel(
    PairedMachineStore pairedMachineStore,
    VendomatRemoteClient remoteClient,
    LanguageService languageService) : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<DeviceCardViewModel> devices = [];

    [ObservableProperty]
    private bool isRefreshing;

    [ObservableProperty]
    private LanguageOption? selectedLanguageOption;

    public IReadOnlyList<LanguageOption> AvailableLanguages => languageService.AvailableLanguages;

    public bool HasDevices => Devices.Count > 0;

    public bool HasNoDevices => !HasDevices;

    public async Task LoadAsync()
    {
        languageService.LanguageChanged -= OnLanguageChanged;
        languageService.LanguageChanged += OnLanguageChanged;
        SelectedLanguageOption = languageService.GetCurrentLanguageOption();

        var records = await pairedMachineStore.GetAllAsync();
        Devices = new ObservableCollection<DeviceCardViewModel>(records
            .OrderBy(item => item.MachineName)
            .Select(CreateCard));

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasNoDevices));
    }

    partial void OnSelectedLanguageOptionChanged(LanguageOption? value)
    {
        if (value is null || value.Code == languageService.CurrentLocale)
        {
            return;
        }

        languageService.SetLanguage(value.Code);
    }

    [RelayCommand]
    private Task OpenScanner() => Shell.Current.GoToAsync("QrScannerPage");

    [RelayCommand]
    private Task OpenDetails(DeviceCardViewModel? device)
    {
        if (device is null)
        {
            return Task.CompletedTask;
        }

        return Shell.Current.GoToAsync($"MachineDetailPage?machineId={device.MachineId}");
    }

    [RelayCommand]
    private Task OpenSettings(DeviceCardViewModel? device)
    {
        if (device is null)
        {
            return Task.CompletedTask;
        }

        return Shell.Current.GoToAsync($"MachineSettingsPage?machineId={device.MachineId}");
    }

    [RelayCommand]
    private async Task RemoveDevice(DeviceCardViewModel? device)
    {
        if (device is null)
        {
            return;
        }

        var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null)
        {
            return;
        }

        var confirmed = await page.DisplayAlertAsync(
            T(nameof(AppLanguageStrings.MobileDevicesRemoveTitle)),
            string.Format(T(nameof(AppLanguageStrings.MobileDevicesRemoveMessageFormat)), device.MachineName),
            T(nameof(AppLanguageStrings.CommonDelete)),
            T(nameof(AppLanguageStrings.CommonCancel)));

        if (!confirmed)
        {
            return;
        }

        await pairedMachineStore.RemoveAsync(device.MachineId);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Refresh()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;

        try
        {
            var records = await pairedMachineStore.GetAllAsync();
            foreach (var record in records)
            {
                try
                {
                    var result = await remoteClient.GetStatusAsync(record);
                    var snapshot = result.Payload;
                    record.RememberSuccessfulConnection(result.ApiBaseUrl, result.ConnectionMode);
                    record.MachineName = snapshot.Settings.MachineName;
                    record.LocalApiBaseUrl = snapshot.Settings.LocalApiBaseUrl;
                    record.PublicApiBaseUrl = snapshot.Settings.PublicApiBaseUrl;
                    record.CloudApiBaseUrl = snapshot.Settings.CloudApiBaseUrl;
                    record.LastSeenOnline = true;
                    record.LastSeenUtc = snapshot.GeneratedAtUtc;
                    record.LastKnownStockLiters = snapshot.Settings.CurrentStockLiters;
                    record.LastKnownTemperatureCelsius = snapshot.Sensor.TemperatureCelsius;
                    record.LastKnownPricePerLiter = snapshot.Settings.PricePerLiter;
                }
                catch
                {
                    record.LastSeenOnline = false;
                }

                await pairedMachineStore.AddOrUpdateAsync(record);
            }

            await LoadAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private DeviceCardViewModel CreateCard(PairedMachineRecord record)
    {
        var card = new DeviceCardViewModel(languageService);
        card.Apply(record);
        return card;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        SelectedLanguageOption = languageService.GetCurrentLanguageOption();
        foreach (var card in Devices)
        {
            card.RefreshLocalized();
        }

        OnPropertyChanged(nameof(AvailableLanguages));
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(HasNoDevices));
    }

    private string T(string key) => languageService.GetText(key);
}
