#include <Arduino.h>
#include <ArduinoJson.h>
#include <DHTesp.h>
#include <HTTPUpdate.h>
#include <WiFi.h>
#include <math.h>
#include <string.h>

#include "FirmwareConfig.h"

namespace
{
#define RXD2 16
#define TXD2 17

constexpr uint8_t MessageTypeSensorSnapshot = 0;
constexpr uint8_t MessageTypeDispenseProgress = 1;
constexpr uint8_t MessageTypeDispenseRequest = 2;
constexpr uint8_t MessageTypeContinuousClean = 3;
constexpr uint8_t MessageTypeStopDispense = 4;
constexpr uint8_t MessageTypePulsedClean = 5;
constexpr uint8_t MessageTypeDispenseCompleted = 11;
constexpr uint8_t MessageTypeFirmwareUpdate = 20;
constexpr uint8_t MessageTypeAcknowledge = 100;

constexpr size_t MessageIdBufferSize = 40;

enum class CommandKind : uint8_t
{
    Invalid = 0,
    Probe = 1,
    Dispense = 2,
    ContinuousClean = 3,
    PulsedClean = 4,
    Stop = 5,
    FirmwareUpdate = 6,
};

struct CommandMessage
{
    CommandKind kind = CommandKind::Invalid;
    float targetLiters = 0.0F;
    uint32_t pulsesPerLiter = FirmwareConfig::DefaultPulsesPerLiter;
    uint32_t durationMs = 0;
    uint32_t pulseOnMs = 0;
    uint32_t pulseOffMs = 0;
    char messageId[MessageIdBufferSize] = {};
    char firmwareUrl[FirmwareConfig::FirmwareUrlBufferSize] = {};
    char wifiSsid[FirmwareConfig::WifiSsidBufferSize] = {};
    char wifiPassword[FirmwareConfig::WifiPasswordBufferSize] = {};
    char expectedMd5[FirmwareConfig::ExpectedMd5BufferSize] = {};
};

struct DispenseState
{
    bool active = false;
    uint32_t startedPulseCount = 0;
    uint32_t targetPulseCount = 0;
    uint32_t pulsesPerLiter = FirmwareConfig::DefaultPulsesPerLiter;
    uint32_t lastProgressSentAtMs = 0;
};

struct SanitationState
{
    bool active = false;
    bool pulsed = false;
    bool relayOn = false;
    uint32_t endAtMs = 0;
    uint32_t pulseOnMs = 0;
    uint32_t pulseOffMs = 0;
    uint32_t nextToggleAtMs = 0;
};

DHTesp g_dhtSensor;
QueueHandle_t g_commandQueue = nullptr;

portMUX_TYPE g_flowPulseMux = portMUX_INITIALIZER_UNLOCKED;
volatile uint32_t g_totalFlowPulses = 0;

DispenseState g_dispenseState;
SanitationState g_sanitationState;

char g_commandLineBuffer[FirmwareConfig::CommandLineBufferSize] = {};
size_t g_commandLineLength = 0;
uint32_t g_lastSensorSampleAtMs = 0;

void IRAM_ATTR onFlowPulse()
{
    portENTER_CRITICAL_ISR(&g_flowPulseMux);
    ++g_totalFlowPulses;
    portEXIT_CRITICAL_ISR(&g_flowPulseMux);
}

uint32_t readFlowPulseCount()
{
    portENTER_CRITICAL(&g_flowPulseMux);
    const uint32_t pulseCount = g_totalFlowPulses;
    portEXIT_CRITICAL(&g_flowPulseMux);
    return pulseCount;
}

uint32_t atLeast(uint32_t value, uint32_t minimumValue)
{
    return value < minimumValue ? minimumValue : value;
}

void copyMessageId(char* destination, size_t destinationSize, const char* source)
{
    if (destinationSize == 0)
    {
        return;
    }

    if (source == nullptr)
    {
        destination[0] = '\0';
        return;
    }

    strncpy(destination, source, destinationSize - 1);
    destination[destinationSize - 1] = '\0';
}

bool hasMessageId(const CommandMessage& command)
{
    return command.messageId[0] != '\0';
}

bool isEmptyText(const char* value)
{
    return value == nullptr || value[0] == '\0';
}

void setPumpRelay(bool enabled)
{
    const auto relayLevel = FirmwareConfig::PumpRelayActiveHigh
        ? (enabled ? HIGH : LOW)
        : (enabled ? LOW : HIGH);

    digitalWrite(FirmwareConfig::PumpRelayPin, relayLevel);
}

template <typename TDocument>
void sendJson(const TDocument& document)
{
    serializeJson(document, Serial);
    Serial.write('\n');
    serializeJson(document, Serial2);
    Serial2.write('\n');
}

void sendAck(const char* action, const char* status, const char* messageId = nullptr)
{
    StaticJsonDocument<224> response;
    response["Type"] = MessageTypeAcknowledge;
    response["Action"] = action;
    response["Status"] = status;
    response["UptimeMs"] = millis();

    if (messageId != nullptr && messageId[0] != '\0')
    {
        response["MsgId"] = messageId;
    }

    sendJson(response);
}

void sendSensorSnapshot(float temperature, float humidity)
{
    StaticJsonDocument<192> payload;
    payload["Type"] = MessageTypeSensorSnapshot;
    payload["Temperature"] = temperature;
    payload["Humidity"] = humidity;
    sendJson(payload);
}

void sendDispenseProgress()
{
    if (!g_dispenseState.active || g_dispenseState.pulsesPerLiter == 0)
    {
        return;
    }

    const uint32_t currentPulseCount = readFlowPulseCount();
    const uint32_t dispensedPulses = currentPulseCount - g_dispenseState.startedPulseCount;
    const float dispensedLiters = static_cast<float>(dispensedPulses) / static_cast<float>(g_dispenseState.pulsesPerLiter);
    const uint32_t volumeMl = static_cast<uint32_t>(roundf(dispensedLiters * 1000.0F));

    StaticJsonDocument<224> payload;
    payload["Type"] = MessageTypeDispenseProgress;
    payload["Volume"] = volumeMl;
    payload["DispensedLiters"] = roundf(dispensedLiters * 1000.0F) / 1000.0F;
    payload["PulseCount"] = dispensedPulses;
    sendJson(payload);
}

void stopAllOutputs()
{
    g_dispenseState.active = false;
    g_sanitationState.active = false;
    g_sanitationState.pulsed = false;
    g_sanitationState.relayOn = false;
    setPumpRelay(false);
}

void sendDispenseCompleted(const char* status)
{
    StaticJsonDocument<160> payload;
    payload["Type"] = MessageTypeDispenseCompleted;
    payload["Status"] = status;
    sendJson(payload);
}

bool tryReadFloat(JsonVariantConst value, float& result)
{
    if (value.is<float>() || value.is<double>() || value.is<long>() || value.is<int>())
    {
        result = value.as<float>();
        return true;
    }

    return false;
}

bool tryReadUInt(JsonVariantConst value, uint32_t& result)
{
    if (value.is<unsigned long>() || value.is<long>() || value.is<unsigned int>() || value.is<int>())
    {
        result = value.as<uint32_t>();
        return true;
    }

    return false;
}

void copyJsonString(JsonVariantConst value, char* destination, size_t destinationSize)
{
    copyMessageId(destination, destinationSize, value | "");
}

bool tryParseCommand(const char* line, CommandMessage& command, const char*& errorStatus)
{
    StaticJsonDocument<512> request;
    const DeserializationError error = deserializeJson(request, line);
    if (error)
    {
        errorStatus = "InvalidJson";
        return false;
    }

    copyMessageId(command.messageId, sizeof(command.messageId), request["MsgId"] | "");

    const int messageType = request["Type"] | -1;
    switch (messageType)
    {
        case MessageTypeAcknowledge:
            command.kind = CommandKind::Probe;
            return true;

        case MessageTypeDispenseRequest:
        {
            float targetLiters = 0.0F;
            if (!tryReadFloat(request["TargetLiters"], targetLiters)
                && !tryReadFloat(request["VolumeLiters"], targetLiters))
            {
                float volumeMl = 0.0F;
                if (tryReadFloat(request["Volume"], volumeMl))
                {
                    targetLiters = volumeMl / 1000.0F;
                }
            }

            if (targetLiters <= 0.0F)
            {
                errorStatus = "InvalidTarget";
                return false;
            }

            uint32_t pulsesPerLiter = FirmwareConfig::DefaultPulsesPerLiter;
            if (!tryReadUInt(request["PulsesPerLiter"], pulsesPerLiter))
            {
                (void)tryReadUInt(request["ImpulseCount"], pulsesPerLiter);
            }

            command.kind = CommandKind::Dispense;
            command.targetLiters = targetLiters;
            command.pulsesPerLiter = atLeast(pulsesPerLiter, 1);
            return true;
        }

        case MessageTypeContinuousClean:
        {
            uint32_t durationSeconds = 0;
            if (!tryReadUInt(request["DurationSeconds"], durationSeconds) || durationSeconds == 0)
            {
                errorStatus = "InvalidDuration";
                return false;
            }

            command.kind = CommandKind::ContinuousClean;
            command.durationMs = durationSeconds * 1000UL;
            return true;
        }

        case MessageTypePulsedClean:
        {
            uint32_t durationSeconds = 0;
            uint32_t pulseOnMs = 0;
            uint32_t pulseOffMs = 0;
            if (!tryReadUInt(request["DurationSeconds"], durationSeconds) || durationSeconds == 0)
            {
                errorStatus = "InvalidDuration";
                return false;
            }

            if (!tryReadUInt(request["PulseOnMilliseconds"], pulseOnMs))
            {
                errorStatus = "InvalidPulseOn";
                return false;
            }

            if (!tryReadUInt(request["PulseOffMilliseconds"], pulseOffMs))
            {
                errorStatus = "InvalidPulseOff";
                return false;
            }

            command.kind = CommandKind::PulsedClean;
            command.durationMs = durationSeconds * 1000UL;
            command.pulseOnMs = atLeast(pulseOnMs, 100);
            command.pulseOffMs = atLeast(pulseOffMs, 100);
            return true;
        }

        case MessageTypeStopDispense:
            command.kind = CommandKind::Stop;
            return true;

        case MessageTypeFirmwareUpdate:
            copyJsonString(request["Url"], command.firmwareUrl, sizeof(command.firmwareUrl));
            copyJsonString(request["WifiSsid"], command.wifiSsid, sizeof(command.wifiSsid));
            copyJsonString(request["WifiPassword"], command.wifiPassword, sizeof(command.wifiPassword));
            copyJsonString(request["ExpectedMd5"], command.expectedMd5, sizeof(command.expectedMd5));

            if (isEmptyText(command.firmwareUrl))
            {
                errorStatus = "InvalidUrl";
                return false;
            }

            command.kind = CommandKind::FirmwareUpdate;
            return true;

        default:
            errorStatus = "UnsupportedType";
            return false;
    }
}

void enqueueOrReject(const CommandMessage& command)
{
    if (g_commandQueue == nullptr)
    {
        sendAck("Queue", "Unavailable", hasMessageId(command) ? command.messageId : nullptr);
        return;
    }

    if (command.kind == CommandKind::Stop)
    {
        CommandMessage ignoredCommand;
        while (xQueueReceive(g_commandQueue, &ignoredCommand, 0) == pdTRUE)
        {
        }
    }

    if (xQueueSend(g_commandQueue, &command, 0) == pdTRUE)
    {
        sendAck("Queue", "Accepted", hasMessageId(command) ? command.messageId : nullptr);
        return;
    }

    sendAck("Queue", "Full", hasMessageId(command) ? command.messageId : nullptr);
}

void processIncomingSerial()
{
    while (Serial2.available() > 0)
    {
        const char currentChar = static_cast<char>(Serial2.read());

        if (currentChar == '\r')
        {
            continue;
        }

        if (currentChar == '\n')
        {
            if (g_commandLineLength == 0)
            {
                continue;
            }

            g_commandLineBuffer[g_commandLineLength] = '\0';

            CommandMessage command;
            const char* errorStatus = nullptr;
            if (tryParseCommand(g_commandLineBuffer, command, errorStatus))
            {
                enqueueOrReject(command);
            }
            else
            {
                sendAck("Parse", errorStatus != nullptr ? errorStatus : "Invalid");
            }

            g_commandLineLength = 0;
            continue;
        }

        if (g_commandLineLength + 1 >= sizeof(g_commandLineBuffer))
        {
            g_commandLineLength = 0;
            sendAck("Parse", "LineTooLong");
            continue;
        }

        g_commandLineBuffer[g_commandLineLength++] = currentChar;
    }
}

void startDispense(const CommandMessage& command)
{
    stopAllOutputs();

    const uint32_t currentPulseCount = readFlowPulseCount();
    const uint32_t targetPulseCount = atLeast(
        static_cast<uint32_t>(roundf(command.targetLiters * static_cast<float>(command.pulsesPerLiter))),
        1);

    g_dispenseState.active = true;
    g_dispenseState.startedPulseCount = currentPulseCount;
    g_dispenseState.targetPulseCount = targetPulseCount;
    g_dispenseState.pulsesPerLiter = command.pulsesPerLiter;
    g_dispenseState.lastProgressSentAtMs = 0;

    setPumpRelay(true);
    sendAck("DispenseRequest", "Started", hasMessageId(command) ? command.messageId : nullptr);
}

void startContinuousClean(const CommandMessage& command)
{
    stopAllOutputs();

    g_sanitationState.active = true;
    g_sanitationState.pulsed = false;
    g_sanitationState.relayOn = true;
    g_sanitationState.endAtMs = millis() + command.durationMs;

    setPumpRelay(true);
    sendAck("ContinuousClean", "Started", hasMessageId(command) ? command.messageId : nullptr);
}

void startPulsedClean(const CommandMessage& command)
{
    stopAllOutputs();

    g_sanitationState.active = true;
    g_sanitationState.pulsed = true;
    g_sanitationState.relayOn = true;
    g_sanitationState.endAtMs = millis() + command.durationMs;
    g_sanitationState.pulseOnMs = command.pulseOnMs;
    g_sanitationState.pulseOffMs = command.pulseOffMs;
    g_sanitationState.nextToggleAtMs = millis() + command.pulseOnMs;

    setPumpRelay(true);
    sendAck("PulsedClean", "Started", hasMessageId(command) ? command.messageId : nullptr);
}

bool ensureWifiConnected(const CommandMessage& command)
{
    if (isEmptyText(command.wifiSsid))
    {
        return WiFi.status() == WL_CONNECTED;
    }

    WiFi.mode(WIFI_STA);
    WiFi.begin(command.wifiSsid, command.wifiPassword);

    const uint32_t startedAtMs = millis();
    while (WiFi.status() != WL_CONNECTED && millis() - startedAtMs < FirmwareConfig::WifiConnectTimeoutMs)
    {
        delay(200);
    }

    return WiFi.status() == WL_CONNECTED;
}

void startFirmwareUpdate(const CommandMessage& command)
{
    if (g_dispenseState.active || g_sanitationState.active)
    {
        sendAck("FirmwareUpdate", "Busy", hasMessageId(command) ? command.messageId : nullptr);
        return;
    }

    sendAck("FirmwareUpdate", "Preparing", hasMessageId(command) ? command.messageId : nullptr);

    if (!ensureWifiConnected(command))
    {
        sendAck("FirmwareUpdate", "WifiFailed", hasMessageId(command) ? command.messageId : nullptr);
        return;
    }

    sendAck("FirmwareUpdate", "Downloading", hasMessageId(command) ? command.messageId : nullptr);

    WiFiClient client;
    HTTPUpdate httpUpdate;
    httpUpdate.rebootOnUpdate(false);

    if (!isEmptyText(command.expectedMd5))
    {
        httpUpdate.setMD5(command.expectedMd5);
    }

    const t_httpUpdate_return result = httpUpdate.update(client, command.firmwareUrl);
    switch (result)
    {
        case HTTP_UPDATE_FAILED:
            sendAck("FirmwareUpdate", "Failed", hasMessageId(command) ? command.messageId : nullptr);
            return;

        case HTTP_UPDATE_NO_UPDATES:
            sendAck("FirmwareUpdate", "NoUpdates", hasMessageId(command) ? command.messageId : nullptr);
            return;

        case HTTP_UPDATE_OK:
            sendAck("FirmwareUpdate", "Rebooting", hasMessageId(command) ? command.messageId : nullptr);
            delay(200);
            ESP.restart();
            return;
    }
}

void processCommandQueue()
{
    if (g_commandQueue == nullptr)
    {
        return;
    }

    CommandMessage command;
    while (xQueueReceive(g_commandQueue, &command, 0) == pdTRUE)
    {
        switch (command.kind)
        {
            case CommandKind::Probe:
                sendAck("Probe", "OK", hasMessageId(command) ? command.messageId : nullptr);
                break;

            case CommandKind::Dispense:
                startDispense(command);
                break;

            case CommandKind::ContinuousClean:
                startContinuousClean(command);
                break;

            case CommandKind::PulsedClean:
                startPulsedClean(command);
                break;

            case CommandKind::Stop:
                stopAllOutputs();
                sendAck("Stop", "Completed", hasMessageId(command) ? command.messageId : nullptr);
                break;

            case CommandKind::FirmwareUpdate:
                startFirmwareUpdate(command);
                break;

            default:
                sendAck("Command", "Invalid", hasMessageId(command) ? command.messageId : nullptr);
                break;
        }
    }
}

void updateDispense()
{
    if (!g_dispenseState.active)
    {
        return;
    }

    const uint32_t now = millis();
    if (now - g_dispenseState.lastProgressSentAtMs >= FirmwareConfig::ProgressIntervalMs)
    {
        g_dispenseState.lastProgressSentAtMs = now;
        sendDispenseProgress();
    }

    const uint32_t currentPulseCount = readFlowPulseCount();
    const uint32_t dispensedPulses = currentPulseCount - g_dispenseState.startedPulseCount;
    if (dispensedPulses < g_dispenseState.targetPulseCount)
    {
        return;
    }

    sendDispenseProgress();
    g_dispenseState.active = false;
    setPumpRelay(false);
    sendDispenseCompleted("Completed");
}

void updateSanitation()
{
    if (!g_sanitationState.active)
    {
        return;
    }

    const uint32_t now = millis();
    if (static_cast<int32_t>(now - g_sanitationState.endAtMs) >= 0)
    {
        g_sanitationState.active = false;
        g_sanitationState.relayOn = false;
        setPumpRelay(false);
        sendAck("Sanitation", "Completed");
        return;
    }

    if (!g_sanitationState.pulsed)
    {
        return;
    }

    if (static_cast<int32_t>(now - g_sanitationState.nextToggleAtMs) < 0)
    {
        return;
    }

    g_sanitationState.relayOn = !g_sanitationState.relayOn;
    setPumpRelay(g_sanitationState.relayOn);
    g_sanitationState.nextToggleAtMs = now + (g_sanitationState.relayOn
        ? g_sanitationState.pulseOnMs
        : g_sanitationState.pulseOffMs);
}

void sampleSensors()
{
    const uint32_t now = millis();
    if (now - g_lastSensorSampleAtMs < FirmwareConfig::SensorIntervalMs)
    {
        return;
    }

    g_lastSensorSampleAtMs = now;

    const TempAndHumidity measurement = g_dhtSensor.getTempAndHumidity();
    if (isnan(measurement.temperature) || isnan(measurement.humidity))
    {
        return;
    }

    sendSensorSnapshot(measurement.temperature, measurement.humidity);
}
}  // namespace

void setup()
{
    Serial.setRxBufferSize(FirmwareConfig::SerialRxBufferSize);
    Serial.setTxBufferSize(FirmwareConfig::SerialTxBufferSize);
    Serial.begin(FirmwareConfig::UsbBaudRate);
    Serial2.setRxBufferSize(FirmwareConfig::SerialRxBufferSize);
    Serial2.setTxBufferSize(FirmwareConfig::SerialTxBufferSize);
    Serial2.begin(FirmwareConfig::TabletBaudRate, SERIAL_8N1, RXD2, TXD2);
    delay(200);

    pinMode(FirmwareConfig::PumpRelayPin, OUTPUT);
    setPumpRelay(false);

    pinMode(FirmwareConfig::FlowPulsePin, INPUT_PULLUP);
    attachInterrupt(digitalPinToInterrupt(FirmwareConfig::FlowPulsePin), onFlowPulse, RISING);

    g_dhtSensor.setup(FirmwareConfig::DhtPin, DHTesp::DHT22);
    g_commandQueue = xQueueCreate(FirmwareConfig::CommandQueueLength, sizeof(CommandMessage));
    g_lastSensorSampleAtMs = millis();

    sendAck("Boot", g_commandQueue != nullptr ? "Ready" : "QueueInitFailed");
}

void loop()
{
    processIncomingSerial();
    processCommandQueue();
    updateDispense();
    updateSanitation();
    sampleSensors();
    delay(1);
}
