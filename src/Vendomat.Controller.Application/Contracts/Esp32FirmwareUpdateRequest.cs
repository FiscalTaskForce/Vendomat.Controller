namespace Vendomat.Controller.Application.Contracts;

public sealed class Esp32FirmwareUpdateRequest
{
    public Guid? CommandId { get; set; }
    public string FirmwareUrl { get; set; } = string.Empty;
    public string WifiSsid { get; set; } = string.Empty;
    public string WifiPassword { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the firmware binary (64 hex chars). The device must verify this before
    /// flashing so a tampered or man-in-the-middle download is rejected. Required.
    /// </summary>
    public string ExpectedSha256 { get; set; } = string.Empty;

    /// <summary>Legacy MD5 checksum. Kept for compatibility; not collision-resistant — prefer <see cref="ExpectedSha256"/>.</summary>
    public string ExpectedMd5 { get; set; } = string.Empty;
}
