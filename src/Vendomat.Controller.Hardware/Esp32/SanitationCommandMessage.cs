namespace Vendomat.Controller.Hardware.Esp32;

public sealed class SanitationCommandMessage : Esp32Envelope
{
    public SanitationCommandMessage()
    {
        Type = Esp32MessageType.ContinuousClean;
    }

    public int DurationSeconds { get; set; }
    public int PulseOnMilliseconds { get; set; }
    public int PulseOffMilliseconds { get; set; }
}
