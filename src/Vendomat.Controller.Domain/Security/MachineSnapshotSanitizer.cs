using System.Text.Json;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Domain.Security;

public static class MachineSnapshotSanitizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static MachineSettings ForExternalApi(MachineSettings settings)
    {
        var clone = Clone(settings);
        SanitizeSettings(clone);
        return clone;
    }

    public static MachineStatusSnapshot ForExternalApi(MachineStatusSnapshot snapshot)
    {
        var clone = Clone(snapshot);
        SanitizeSettings(clone.Settings);
        return clone;
    }

    public static MachineDashboardSnapshot ForExternalApi(MachineDashboardSnapshot dashboard)
    {
        var clone = Clone(dashboard);
        SanitizeSettings(clone.Status.Settings);
        return clone;
    }

    public static void SanitizeSettings(MachineSettings settings)
    {
        settings.CloudMachineToken = string.Empty;
        settings.CompanionAccessToken = string.Empty;
        settings.AdminPasscodeHash = string.Empty;
    }

    private static T Clone<T>(T value) where T : class =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)
        ?? throw new InvalidOperationException($"Could not clone {typeof(T).Name}.");
}
