using Vendomat.Controller.Tablet.Pages;

namespace Vendomat.Controller.Tablet;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
    }
}
