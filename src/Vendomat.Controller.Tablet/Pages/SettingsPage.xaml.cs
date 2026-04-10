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
}
