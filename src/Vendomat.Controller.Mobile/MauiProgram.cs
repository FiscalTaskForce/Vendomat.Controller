using Microsoft.Extensions.Logging;
using Syncfusion.Maui.Core.Hosting;
using Vendomat.Controller.Client.Localization;
using Vendomat.Controller.Mobile.Pages;
using Vendomat.Controller.Mobile.Services;
using Vendomat.Controller.Mobile.ViewModels;
using ZXing.Net.Maui.Controls;

namespace Vendomat.Controller.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        SyncfusionLicenseRegistrar.Register();
        var builder = MauiApp.CreateBuilder();
        builder.ConfigureSyncfusionCore();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<LanguageService>();
        builder.Services.AddSingleton<DeviceSecretStore>();
        builder.Services.AddSingleton<PairedMachineStore>();
        builder.Services.AddSingleton<HttpClient>();
        builder.Services.AddSingleton<VendomatRemoteClient>();

        builder.Services.AddTransient<MainPageViewModel>();
        builder.Services.AddTransient<QrScannerViewModel>();
        builder.Services.AddTransient<MachineDetailViewModel>();
        builder.Services.AddTransient<MachineSettingsViewModel>();

        builder.Services.AddTransient<QrScannerPage>();
        builder.Services.AddTransient<MachineDetailPage>();
        builder.Services.AddTransient<MachineSettingsPage>();

        var app = builder.Build();
        _ = app.Services.GetRequiredService<LanguageService>();
        ServiceRegistry.Initialize(app.Services);
        return app;
    }
}
