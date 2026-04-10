using Syncfusion.Licensing;

namespace Vendomat.Controller.Client.Localization;

public static class SyncfusionLicenseRegistrar
{
    private const string LicenseKey = "Ngo9BigBOggjHTQxAR8/V1JHaF5cWWdCf1FpRmJGdld5fUVHYVZUTXxaS00DNHVRdkdlWXteeXVWRmdZUkV1X0RWYEo=";

    public static void Register() => SyncfusionLicenseProvider.RegisterLicense(LicenseKey);
}
