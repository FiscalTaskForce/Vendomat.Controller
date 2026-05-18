using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vendomat.Controller.Mobile.Services;

public sealed class FlexibleDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (TryRead(document.RootElement, out var value))
        {
            return value;
        }

        throw new JsonException("The JSON value could not be converted to System.DateTimeOffset.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    public static bool TryRead(JsonElement element, out DateTimeOffset value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return TryParseString(element.GetString(), out value);

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var unixSeconds))
                {
                    value = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                    return true;
                }

                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("UtcDateTime", out var utcDateTimeElement)
                    && TryParseString(utcDateTimeElement.GetString(), out value))
                {
                    return true;
                }

                if (element.TryGetProperty("DateTime", out var dateTimeElement)
                    && TryParseString(dateTimeElement.GetString(), out value))
                {
                    return true;
                }

                if (element.TryGetProperty("Ticks", out var ticksElement)
                    && ticksElement.ValueKind == JsonValueKind.Number
                    && ticksElement.TryGetInt64(out var ticks))
                {
                    var offset = TimeSpan.Zero;
                    if (element.TryGetProperty("Offset", out var offsetElement)
                        && offsetElement.ValueKind == JsonValueKind.String
                        && TimeSpan.TryParse(offsetElement.GetString(), CultureInfo.InvariantCulture, out var parsedOffset))
                    {
                        offset = parsedOffset;
                    }

                    value = new DateTimeOffset(ticks, offset);
                    return true;
                }

                break;
        }

        value = default;
        return false;
    }

    private static bool TryParseString(string? rawValue, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
}

public sealed class NullableFlexibleDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return FlexibleDateTimeOffsetJsonConverter.TryRead(document.RootElement, out var value)
            ? value
            : null;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value);
    }
}
