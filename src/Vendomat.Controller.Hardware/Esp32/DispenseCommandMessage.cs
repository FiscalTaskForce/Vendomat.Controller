namespace Vendomat.Controller.Hardware.Esp32;

public sealed class DispenseCommandMessage : Esp32Envelope
{
    public DispenseCommandMessage()
    {
        Type = Esp32MessageType.DispenseRequest;
    }

    public decimal TargetLiters { get; set; }
    public int PulsesPerLiter { get; set; }
}
