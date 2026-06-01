namespace Vendomat.Controller.Application.Contracts;

public sealed class Esp32FirmwareUpdateRequest
{
    public Guid? CommandId { get; set; }
    public string FirmwareUrl { get; set; } = string.Empty;
    public string WifiSsid { get; set; } = string.Empty;
    public string WifiPassword { get; set; } = string.Empty;
    public string ExpectedMd5 { get; set; } = string.Empty;
}
