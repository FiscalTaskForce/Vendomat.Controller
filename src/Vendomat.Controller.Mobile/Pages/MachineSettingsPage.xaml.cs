using Vendomat.Controller.Mobile.ViewModels;

namespace Vendomat.Controller.Mobile.Pages;

[QueryProperty(nameof(MachineId), "machineId")]
public partial class MachineSettingsPage : ContentPage
{
    private MachineSettingsViewModel ViewModel => (MachineSettingsViewModel)BindingContext;

    public MachineSettingsPage()
    {
        InitializeComponent();
        BindingContext = ServiceRegistry.GetRequiredService<MachineSettingsViewModel>();
    }

    public string MachineId
    {
        set
        {
            if (Guid.TryParse(value, out var machineId))
            {
                _ = ViewModel.LoadAsync(machineId);
            }
        }
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
