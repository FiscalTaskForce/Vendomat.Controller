namespace Vendomat.Controller.Hardware.Esp32;

public class Esp32Envelope
{
    public Esp32MessageType Type { get; set; }
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
}
