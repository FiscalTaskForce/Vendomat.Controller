namespace Vendomat.Controller.Domain.Models;

/// <summary>
/// Canonical values for <see cref="DeviceLogEntry.Category"/>. Using shared constants
/// instead of inline string literals prevents typos and keeps log filtering reliable.
/// </summary>
public static class LogCategories
{
    public const string Esp32 = "ESP32";
    public const string Validator = "Validator";
    public const string Cash = "Cash";
    public const string Priming = "Priming";
    public const string Dispense = "Dispense";
    public const string Sanitation = "Sanitation";
    public const string Settings = "Settings";
    public const string RemoteCredit = "RemoteCredit";
    public const string Pairing = "Pairing";
    public const string Background = "Background";
}
