namespace Vendomat.Controller.Hardware.Esp32;

public enum Esp32MessageType
{
    SensorSnapshot = 0,
    DispenseProgress = 1,
    DispenseRequest = 2,
    ContinuousClean = 3,
    StopDispense = 4,
    PulsedClean = 5,
    Acknowledge = 100,
}
