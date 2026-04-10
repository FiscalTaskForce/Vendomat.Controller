using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vendomat.Controller.Hardware.Esp32;

public static class Esp32ProtocolSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public static string Serialize<TMessage>(TMessage message) where TMessage : Esp32Envelope =>
        JsonSerializer.Serialize(message, Options);
}
