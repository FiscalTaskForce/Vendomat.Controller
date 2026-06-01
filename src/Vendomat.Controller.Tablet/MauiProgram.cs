using Android.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SQLitePCL;
using Syncfusion.Maui.Core.Hosting;
using Vendomat.Controller.Client.Localization;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Hardware.Services;
using Vendomat.Controller.Tablet.Pages;
using Vendomat.Controller.Tablet.Services;
using Vendomat.Controller.Tablet.ViewModels;

namespace Vendomat.Controller.Tablet;

public static class MauiProgram
{
    private const string StartupTag = "VendomatStartup";

    public static MauiApp CreateMauiApp()
    {
        Log.Info(StartupTag, "CreateMauiApp start");
        Batteries.Init();
        SyncfusionLicenseRegistrar.Register();
        var builder = MauiApp.CreateBuilder();
        builder.ConfigureSyncfusionCore();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<LocalDatabaseService>();
        builder.Services.AddSingleton<LocalApiSecurityService>();
        builder.Services.AddSingleton<DeviceSecretStore>();
        builder.Services.AddSingleton<RemoteCommandJournal>();
        builder.Services.AddSingleton<OperationalBackupService>();
        builder.Services.AddSingleton<LanguageService>();
        builder.Services.AddSingleton<IMachineSettingsRepository, SqliteMachineSettingsRepository>();
        builder.Services.AddSingleton<ISalesRepository, SqliteSalesRepository>();
        builder.Services.AddSingleton<ILogRepository, SqliteLogRepository>();
        builder.Services.AddSingleton<ISanitationRepository, SqliteSanitationRepository>();
        builder.Services.AddSingleton<IAdvertisementRepository, SqliteAdvertisementRepository>();
        builder.Services.AddSingleton<IPairingService, PairingService>();
        builder.Services.AddSingleton<IMachineRuntimeService, MachineRuntimeService>();
        builder.Services.AddSingleton<IBillValidatorGateway, Nv9BillValidatorGateway>();
        builder.Services.AddSingleton<IEsp32Gateway, Esp32SerialGateway>();
        builder.Services.AddSingleton<ILocalApiHost, LocalApiHostService>();
        builder.Services.AddSingleton<IKioskDisplayService, KioskDisplayService>();
        builder.Services.AddSingleton(_ => new CloudBrokerClient(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        }));
        builder.Services.AddSingleton<CloudBridgeService>();

        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<SettingsPageViewModel>();
        builder.Services.AddTransient<SettingsPage>();

        Log.Info(StartupTag, "Building Maui app");
        var app = builder.Build();
        _ = app.Services.GetRequiredService<LanguageService>();
        ServiceRegistry.Initialize(app.Services);
        Log.Info(StartupTag, "Local database initialization deferred");
        Log.Info(StartupTag, "CreateMauiApp complete");

        return app;
    }
}
