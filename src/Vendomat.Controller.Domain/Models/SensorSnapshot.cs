namespace Vendomat.Controller.Domain.Models;

public sealed class SensorSnapshot
{
    public float TemperatureCelsius { get; set; } = 4.2f;
    public float HumidityPercent { get; set; } = 58f;
    public bool FlowSensorOnline { get; set; } = true;
    public bool PumpOnline { get; set; } = true;
}
