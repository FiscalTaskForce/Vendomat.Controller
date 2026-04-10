using System.Text.Json;

namespace Vendomat.Controller.Domain.Models;

public sealed class PairingQrPayload
{
    public int PayloadVersion { get; set; } = 2;
    public Guid MachineId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string PairingCode { get; set; } = string.Empty;
    public string LocalApiBaseUrl { get; set; } = string.Empty;
    public string PublicApiBaseUrl { get; set; } = string.Empty;
    public string CloudApiBaseUrl { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(10);

    public string ToQrPayloadJson()
    {
        var payload = new Dictionary<string, object?>
        {
            ["v"] = PayloadVersion,
            ["m"] = MachineId.ToString("N"),
            ["c"] = PairingCode,
            ["e"] = ExpiresAtUtc.ToUnixTimeSeconds(),
        };

        if (!string.IsNullOrWhiteSpace(LocalApiBaseUrl))
        {
            payload["l"] = LocalApiBaseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(PublicApiBaseUrl))
        {
            payload["p"] = PublicApiBaseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(CloudApiBaseUrl))
        {
            payload["x"] = CloudApiBaseUrl.Trim();
        }

        return JsonSerializer.Serialize(payload);
    }

    public static bool TryParse(string? rawValue, out PairingQrPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawValue);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;
            var expiresAtUtc = ReadDateTimeOffset(root, "ExpiresAtUtc")
                ?? ReadUnixDateTimeOffset(root, "e")
                ?? DateTimeOffset.MinValue;

            payload = new PairingQrPayload
            {
                PayloadVersion = ReadInt32(root, "PayloadVersion")
                    ?? ReadInt32(root, "v")
                    ?? 2,
                MachineId = ReadGuid(root, "MachineId")
                    ?? ReadGuid(root, "m")
                    ?? Guid.Empty,
                MachineName = ReadString(root, "MachineName")
                    ?? ReadString(root, "n")
                    ?? string.Empty,
                PairingCode = ReadString(root, "PairingCode")
                    ?? ReadString(root, "c")
                    ?? string.Empty,
                LocalApiBaseUrl = ReadString(root, "LocalApiBaseUrl")
                    ?? ReadString(root, "l")
                    ?? string.Empty,
                PublicApiBaseUrl = ReadString(root, "PublicApiBaseUrl")
                    ?? ReadString(root, "p")
                    ?? string.Empty,
                CloudApiBaseUrl = ReadString(root, "CloudApiBaseUrl")
                    ?? ReadString(root, "x")
                    ?? string.Empty,
                IssuedAtUtc = ReadDateTimeOffset(root, "IssuedAtUtc")
                    ?? ReadUnixDateTimeOffset(root, "i")
                    ?? DateTimeOffset.UtcNow,
                ExpiresAtUtc = expiresAtUtc,
            };

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? ReadInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(element.GetString(), out var value) => value,
            _ => null,
        };
    }

    private static Guid? ReadGuid(JsonElement root, string propertyName)
    {
        var value = ReadString(root, propertyName);
        return Guid.TryParse(value, out var guid) ? guid : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement root, string propertyName)
    {
        var value = ReadString(root, propertyName);
        return DateTimeOffset.TryParse(value, out var dateTimeOffset) ? dateTimeOffset : null;
    }

    private static DateTimeOffset? ReadUnixDateTimeOffset(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var value) => DateTimeOffset.FromUnixTimeSeconds(value),
            JsonValueKind.String when long.TryParse(element.GetString(), out var value) => DateTimeOffset.FromUnixTimeSeconds(value),
            _ => null,
        };
    }
}
