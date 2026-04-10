using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Vendomat.Controller.Mobile.Models;
using Vendomat.Controller.Mobile.Services;

namespace Vendomat.Controller.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        PublishManualPairingIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        PublishManualPairingIntent(intent);
    }

    private static void PublishManualPairingIntent(Intent? intent)
    {
        if (intent?.Extras is null)
        {
            return;
        }

        var request = new ManualPairingLaunchRequest
        {
            RawPayload = intent.GetStringExtra("vendomat_manual_payload") ?? string.Empty,
            MachineId = intent.GetStringExtra("vendomat_machine_id") ?? string.Empty,
            PairingCode = intent.GetStringExtra("vendomat_pairing_code") ?? string.Empty,
            CloudApiBaseUrl = intent.GetStringExtra("vendomat_cloud_api_base_url") ?? string.Empty,
            PublicApiBaseUrl = intent.GetStringExtra("vendomat_public_api_base_url") ?? string.Empty,
            LocalApiBaseUrl = intent.GetStringExtra("vendomat_local_api_base_url") ?? string.Empty,
            AutoSubmit = string.Equals(intent.GetStringExtra("vendomat_auto_submit"), "1", StringComparison.Ordinal)
                || intent.GetBooleanExtra("vendomat_auto_submit", false),
        };

        if (!request.HasAnyValue)
        {
            return;
        }

        ManualPairingLaunchBridge.Publish(request);
    }
}
