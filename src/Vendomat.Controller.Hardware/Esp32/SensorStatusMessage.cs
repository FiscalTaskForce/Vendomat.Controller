namespace Vendomat.Controller.Hardware.Esp32;

public sealed class SensorStatusMessage : Esp32Envelope
{
    public SensorStatusMessage()
    {
        Type = Esp32MessageType.SensorSnapshot;
    }

    public float Temperature { get; set; }
    public float Humidity { get; set; }
}
