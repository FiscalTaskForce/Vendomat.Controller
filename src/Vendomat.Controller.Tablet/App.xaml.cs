using Android.Util;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Tablet.Services;

namespace Vendomat.Controller.Tablet;

public partial class App : Microsoft.Maui.Controls.Application
{
    private const string StartupTag = "VendomatStartup";

    public App()
    {
        Log.Info(StartupTag, "App ctor start");
        InitializeComponent();
        Log.Info(StartupTag, "App ctor complete");
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Log.Info(StartupTag, "CreateWindow start");
        var window = new Window(new AppShell());
        window.Created += OnWindowCreated;
        Log.Info(StartupTag, "CreateWindow complete");
        return window;
    }

    private async void OnWindowCreated(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Created -= OnWindowCreated;
        }

        Log.Info(StartupTag, "Window created");

        try
        {
            await ServiceRegistry.GetRequiredService<IKioskDisplayService>().EnterImmersiveModeAsync();
            Log.Info(StartupTag, "Immersive mode applied");

            _ = Task.Run(async () =>
            {
                try
                {
                    Log.Info(StartupTag, "Starting local API host");
                    await ServiceRegistry.GetRequiredService<ILocalApiHost>().StartAsync();
                    Log.Info(StartupTag, "Local API host started");
                }
                catch (Exception ex)
                {
                    Log.Error(StartupTag, $"Local API host failed: {ex}");
                }
            });

            _ = Task.Run(() =>
            {
                try
                {
                    Log.Info(StartupTag, "Starting cloud bridge");
                    ServiceRegistry.GetRequiredService<CloudBridgeService>().Start();
                    Log.Info(StartupTag, "Cloud bridge started");
                }
                catch (Exception ex)
                {
                    Log.Error(StartupTag, $"Cloud bridge failed: {ex}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(StartupTag, $"Window startup failed: {ex}");
        }
    }
}
