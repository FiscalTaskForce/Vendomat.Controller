namespace Vendomat.Controller.Domain.Models;

public sealed class MachineStatusSnapshot
{
    public MachineSettings Settings { get; set; } = new();
    public SensorSnapshot Sensor { get; set; } = new();
    public DispenseSessionState Session { get; set; } = new();
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
