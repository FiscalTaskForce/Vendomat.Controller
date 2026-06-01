#pragma once

#include <Arduino.h>

namespace FirmwareConfig
{
constexpr uint32_t UsbBaudRate = 115200;
constexpr uint32_t TabletBaudRate = 115200;
constexpr uint8_t DhtPin = 27;
constexpr uint8_t FlowPulsePin = 23;
constexpr uint8_t PumpRelayPin = 25;
constexpr bool PumpRelayActiveHigh = true;
constexpr size_t SerialRxBufferSize = 2048;
constexpr size_t SerialTxBufferSize = 1024;
constexpr size_t CommandLineBufferSize = 384;
constexpr size_t CommandQueueLength = 12;
constexpr size_t FirmwareUrlBufferSize = 256;
constexpr size_t WifiSsidBufferSize = 64;
constexpr size_t WifiPasswordBufferSize = 64;
constexpr size_t ExpectedMd5BufferSize = 48;
constexpr uint32_t SensorIntervalMs = 4000;
constexpr uint32_t ProgressIntervalMs = 500;
constexpr uint32_t WifiConnectTimeoutMs = 20000;
constexpr uint32_t DefaultPulsesPerLiter = 450;
}  // namespace FirmwareConfig
