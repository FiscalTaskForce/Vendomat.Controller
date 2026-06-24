using Microsoft.Maui.ApplicationModel;
using Vendomat.Controller.Client.Localization;
using Vendomat.Controller.Tablet.ViewModels;

namespace Vendomat.Controller.Tablet.Pages;

public partial class SettingsPage : ContentPage
{
    private SettingsPageViewModel ViewModel => (SettingsPageViewModel)BindingContext;

    public SettingsPage()
    {
        InitializeComponent();
        BindingContext = ServiceRegistry.GetRequiredService<SettingsPageViewModel>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ViewModel.LoadAsync();
    }

    protected override void OnDisappearing()
    {
        ViewModel.Stop();
        base.OnDisappearing();
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }

    private async void OnExitAppClicked(object? sender, EventArgs e)
    {
        var lang = LanguageService.Current;
        var title = lang?.GetText(nameof(AppLanguageStrings.SettingsExitConfirmTitle)) ?? "Exit application";
        var message = lang?.GetText(nameof(AppLanguageStrings.SettingsExitConfirmMessage)) ?? "Are you sure you want to close the app?";
        var accept = lang?.GetText(nameof(AppLanguageStrings.SettingsExitAppButton)) ?? "Exit application";
        var cancel = lang?.GetText(nameof(AppLanguageStrings.CommonCancel)) ?? "Cancel";

        if (!await DisplayAlert(title, message, accept, cancel))
        {
            return;
        }

#if ANDROID
        // Leave the kiosk back to the launcher and tear down the app's task.
        Platform.CurrentActivity?.FinishAffinity();
#endif
        Microsoft.Maui.Controls.Application.Current?.Quit();
    }
}
