using Vendomat.Controller.Mobile.Pages;

namespace Vendomat.Controller.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("QrScannerPage", typeof(QrScannerPage));
        Routing.RegisterRoute("MachineDetailPage", typeof(MachineDetailPage));
        Routing.RegisterRoute("MachineSettingsPage", typeof(MachineSettingsPage));
    }
}
