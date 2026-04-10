using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace Vendomat.Controller.Tablet;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ScreenOrientation = ScreenOrientation.Portrait, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ApplyImmersiveMode();
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplyImmersiveMode();
    }

    private void ApplyImmersiveMode()
    {
        if (Window is null)
        {
            return;
        }

        WindowCompat.SetDecorFitsSystemWindows(Window, false);
        var controller = WindowCompat.GetInsetsController(Window, Window.DecorView);
        controller?.Hide(WindowInsetsCompat.Type.SystemBars());
        if (controller is not null)
        {
            controller.SystemBarsBehavior = (int)WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
        }
    }
}
