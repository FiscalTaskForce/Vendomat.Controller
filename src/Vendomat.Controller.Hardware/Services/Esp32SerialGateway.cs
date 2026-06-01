using System.IO.Ports;
using System.Text;
using System.Text.Json;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Hardware.Services;

public sealed class Esp32SerialGateway : IEsp32Gateway
{
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(6);
    private readonly SemaphoreSlim _sync = new(1, 1);

    private SerialPort? _port;
    private CancellationTokenSource? _readerCancellationTokenSource;
    private Task? _readerTask;
    private string? _connectedPortName;
    private int _connectedBaudRate;
    private bool _autoDiscoverEnabled;

    public event Action<SensorSnapshot>? SensorSnapshotReceived;
    public event Action<decimal>? DispenseProgressReceived;
    public event Action? DispenseCompleted;
    public event Action<string>? PortDetected;

    public async Task StartAsync(string preferredPortName, int baudRate, bool autoDiscover, CancellationToken cancellationToken = default)
    {
        var normalizedPortName = string.IsNullOrWhiteSpace(preferredPortName) ? "/dev/ttyS3" : preferredPortName.Trim();
        var normalizedBaudRate = baudRate <= 0 ? 115200 : baudRate;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_port is not null
                && _port.IsOpen
                && _readerTask is { IsCompleted: false }
                && string.Equals(_connectedPortName, normalizedPortName, StringComparison.OrdinalIgnoreCase)
                && _connectedBaudRate == normalizedBaudRate
                && _autoDiscoverEnabled == autoDiscover)
            {
                return;
            }

            await StopCoreAsync();

            SerialPort? fallbackPort = null;
            string? fallbackPortName = null;

            foreach (var candidatePort in BuildCandidatePorts(normalizedPortName, autoDiscover))
            {
                var result = await TryOpenAndProbeAsync(candidatePort, normalizedBaudRate, cancellationToken);
                if (result.Success && result.Port is not null)
                {
                    fallbackPort?.Dispose();
                    AttachPort(result.Port, candidatePort, normalizedBaudRate, autoDiscover);
                    return;
                }

                if (fallbackPort is null
                    && result.OpenedWithoutSignal
                    && result.Port is not null
                    && string.Equals(candidatePort, normalizedPortName, StringComparison.OrdinalIgnoreCase))
                {
                    fallbackPort = result.Port;
                    fallbackPortName = candidatePort;
                    continue;
                }

                result.Port?.Dispose();
            }

            if (fallbackPort is not null && fallbackPortName is not null)
            {
                Console.WriteLine($"[ESP32] Falling back to preferred port without probe confirmation: {fallbackPortName}");
                AttachPort(fallbackPort, fallbackPortName, normalizedBaudRate, autoDiscover);
                return;
            }

            throw new InvalidOperationException("ESP32 controller was not detected on the configured serial ports.");
        }
        finally
        {
            _sync.Release();
        }
    }

    public Task SendDispenseRequestAsync(decimal targetLiters, int pulsesPerLiter, CancellationToken cancellationToken = default)
    {
        var targetMilliliters = Math.Max(0, Math.Round(targetLiters * 1000m, 2));
        var payload = new
        {
            Type = 2,
            MsgId = Guid.NewGuid().ToString("N"),
            Volume = targetMilliliters,
            VolumeLiters = Math.Round(targetLiters, 3),
            TargetLiters = Math.Round(targetLiters, 3),
            ImpulseCount = pulsesPerLiter,
            PulsesPerLiter = pulsesPerLiter,
        };

        return WritePayloadAsync(payload, cancellationToken);
    }

    public Task SendSanitationAsync(SanitationMode mode, TimeSpan duration, TimeSpan pulseOn, TimeSpan pulseOff, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            Type = mode == SanitationMode.Pulsed ? 5 : 3,
            MsgId = Guid.NewGuid().ToString("N"),
            Mode = mode.ToString(),
            DurationSeconds = Math.Max(0, (int)Math.Round(duration.TotalSeconds)),
            PulseOnMilliseconds = Math.Max(0, (int)Math.Round(pulseOn.TotalMilliseconds)),
            PulseOffMilliseconds = Math.Max(0, (int)Math.Round(pulseOff.TotalMilliseconds)),
        };

        return WritePayloadAsync(payload, cancellationToken);
    }

    public Task SendFirmwareUpdateAsync(Esp32FirmwareUpdateRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var firmwareUrl = request.FirmwareUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(firmwareUrl))
        {
            throw new InvalidOperationException("URL-ul firmware-ului ESP32 este obligatoriu.");
        }

        var payload = new
        {
            Type = 20,
            MsgId = request.CommandId?.ToString("N") ?? Guid.NewGuid().ToString("N"),
            Url = firmwareUrl,
            WifiSsid = request.WifiSsid?.Trim() ?? string.Empty,
            WifiPassword = request.WifiPassword ?? string.Empty,
            ExpectedMd5 = request.ExpectedMd5?.Trim() ?? string.Empty,
        };

        return WritePayloadAsync(payload, cancellationToken);
    }

    public Task StopDispenseAsync(CancellationToken cancellationToken = default) =>
        WritePayloadAsync(new
        {
            Type = 4,
            MsgId = Guid.NewGuid().ToString("N"),
        }, cancellationToken);

    private void AttachPort(SerialPort port, string portName, int baudRate, bool autoDiscover)
    {
        _port = port;
        _connectedPortName = portName;
        _connectedBaudRate = baudRate;
        _autoDiscoverEnabled = autoDiscover;

        _readerCancellationTokenSource = new CancellationTokenSource();
        _readerTask = Task.Run(() => ReadLoopAsync(port, _readerCancellationTokenSource.Token));

        Console.WriteLine($"[ESP32] Connected on {portName} @ {baudRate}");
        PortDetected?.Invoke(portName);
    }

    private async Task<ProbeResult> TryOpenAndProbeAsync(string portName, int baudRate, CancellationToken cancellationToken)
    {
        SerialPort? port = null;

        try
        {
            port = CreatePort(portName, baudRate);
            port.Open();
            Console.WriteLine($"[ESP32] Probing {portName} @ {baudRate}");

            await Task.Delay(500, cancellationToken);

            if (port.BytesToRead > 0)
            {
                _ = port.ReadExisting();
            }

            await WritePayloadAsync(port, new
            {
                Type = 100,
                MsgId = Guid.NewGuid().ToString("N"),
                Probe = true,
            }, cancellationToken);

            var deadlineUtc = DateTimeOffset.UtcNow.Add(ProbeWindow);
            while (DateTimeOffset.UtcNow < deadlineUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? line = null;
                try
                {
                    line = port.ReadLine();
                }
                catch (TimeoutException)
                {
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!TryParseMessage(line, out var message))
                {
                    continue;
                }

                DispatchMessage(message);
                if (message.IsConfirmationSignal)
                {
                    return new ProbeResult(true, true, port);
                }
            }

            return new ProbeResult(false, true, port);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Console.WriteLine($"[ESP32] Probe failed on {portName}: {ex.Message}");
            port?.Dispose();
            return new ProbeResult(false, false, null);
        }
    }

    private async Task ReadLoopAsync(SerialPort port, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = null;
                try
                {
                    line = port.ReadLine();
                }
                catch (TimeoutException)
                {
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (TryParseMessage(line, out var message))
                {
                    DispatchMessage(message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Console.WriteLine($"[ESP32] Read loop stopped: {ex.Message}");
        }
    }

    private void DispatchMessage(ParsedEsp32Message message)
    {
        switch (message.Kind)
        {
            case ParsedEsp32MessageKind.SensorSnapshot when message.SensorSnapshot is not null:
                SensorSnapshotReceived?.Invoke(message.SensorSnapshot);
                break;
            case ParsedEsp32MessageKind.DispenseProgress when message.DispensedLiters.HasValue:
                DispenseProgressReceived?.Invoke(message.DispensedLiters.Value);
                break;
            case ParsedEsp32MessageKind.DispenseCompleted:
                DispenseCompleted?.Invoke();
                break;
        }
    }

    private async Task WritePayloadAsync(object payload, CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (_port is null || !_port.IsOpen)
            {
                throw new InvalidOperationException("ESP32 serial port is not connected.");
            }

            await WritePayloadAsync(_port, payload, cancellationToken);
        }
        finally
        {
            _sync.Release();
        }
    }

    private static Task WritePayloadAsync(SerialPort port, object payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(payload) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);
        port.Write(bytes, 0, bytes.Length);
        Console.WriteLine($"[ESP32] Sent: {json.Trim()}");
        return Task.CompletedTask;
    }

    private async Task StopCoreAsync()
    {
        if (_readerCancellationTokenSource is not null)
        {
            _readerCancellationTokenSource.Cancel();
        }

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_port is not null)
        {
            try
            {
                if (_port.IsOpen)
                {
                    _port.Close();
                }
            }
            catch
            {
            }

            _port.Dispose();
        }

        _readerTask = null;
        _readerCancellationTokenSource?.Dispose();
        _readerCancellationTokenSource = null;
        _port = null;
        _connectedPortName = null;
        _connectedBaudRate = 0;
        _autoDiscoverEnabled = false;
    }

    private static SerialPort CreatePort(string portName, int baudRate)
    {
        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            NewLine = "\n",
            ReadTimeout = 500,
            WriteTimeout = 1000,
            DtrEnable = false,
            RtsEnable = false,
        };

        return port;
    }

    private static IReadOnlyList<string> BuildCandidatePorts(string preferredPortName, bool autoDiscover)
    {
        var candidates = new List<string>();

        void Add(string? portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                return;
            }

            if (candidates.Any(existing => string.Equals(existing, portName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            candidates.Add(portName);
        }

        Add(preferredPortName);

        if (!autoDiscover)
        {
            return candidates;
        }

        if (OperatingSystem.IsWindows())
        {
            Add("COM4");
            foreach (var portName in SerialPort.GetPortNames().OrderBy(static x => x, StringComparer.OrdinalIgnoreCase))
            {
                Add(portName);
            }
        }
        else
        {
            Add("/dev/ttyS1");
            Add("/dev/ttyS3");
            Add("/dev/ttyS4");
            Add("/dev/ttyS5");
            Add("/dev/ttyS7");
            Add("/dev/ttyUSB0");
            Add("/dev/ttyUSB1");
            Add("/dev/ttyACM0");
        }

        return candidates;
    }

    private static bool TryParseMessage(string line, out ParsedEsp32Message message)
    {
        message = default;

        var json = ExtractJson(line);
        if (json is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Type", out var typeElement) || !typeElement.TryGetInt32(out var type))
            {
                return false;
            }

            switch (type)
            {
                case 0:
                    var temperature = TryGetSingle(document.RootElement, "Temperature");
                    var humidity = TryGetSingle(document.RootElement, "Humidity")
                        ?? TryGetSingle(document.RootElement, "Hunidity");

                    message = new ParsedEsp32Message
                    {
                        Kind = ParsedEsp32MessageKind.SensorSnapshot,
                        SensorSnapshot = new SensorSnapshot
                        {
                            TemperatureCelsius = temperature ?? 0,
                            HumidityPercent = humidity ?? 0,
                            FlowSensorOnline = true,
                            PumpOnline = true,
                        },
                        IsConfirmationSignal = true,
                    };
                    return true;

                case 1:
                    var volume = TryGetDecimal(document.RootElement, "Volume");
                    var dispensedLiters = volume.HasValue
                        ? Math.Round(volume.Value / 1000m, 3)
                        : TryGetDecimal(document.RootElement, "DispensedLiters");

                    message = new ParsedEsp32Message
                    {
                        Kind = ParsedEsp32MessageKind.DispenseProgress,
                        DispensedLiters = dispensedLiters,
                        IsConfirmationSignal = dispensedLiters.HasValue,
                    };
                    return dispensedLiters.HasValue;

                case 11:
                    message = new ParsedEsp32Message
                    {
                        Kind = ParsedEsp32MessageKind.DispenseCompleted,
                        IsConfirmationSignal = true,
                    };
                    return true;

                case 100:
                    message = new ParsedEsp32Message
                    {
                        Kind = ParsedEsp32MessageKind.Acknowledge,
                        IsConfirmationSignal = true,
                    };
                    return true;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractJson(string line)
    {
        var startIndex = line.IndexOf('{');
        var endIndex = line.LastIndexOf('}');
        if (startIndex < 0 || endIndex <= startIndex)
        {
            return null;
        }

        return line.Substring(startIndex, endIndex - startIndex + 1);
    }

    private static float? TryGetSingle(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetSingle(out var singleValue) => singleValue,
            JsonValueKind.String when float.TryParse(property.GetString(), out var stringValue) => stringValue,
            _ => null,
        };
    }

    private static decimal? TryGetDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.String when decimal.TryParse(property.GetString(), out var stringValue) => stringValue,
            _ => null,
        };
    }

    private readonly record struct ProbeResult(bool Success, bool OpenedWithoutSignal, SerialPort? Port);

    private readonly record struct ParsedEsp32Message
    {
        public ParsedEsp32MessageKind Kind { get; init; }
        public SensorSnapshot? SensorSnapshot { get; init; }
        public decimal? DispensedLiters { get; init; }
        public bool IsConfirmationSignal { get; init; }
    }

    private enum ParsedEsp32MessageKind
    {
        Unknown = 0,
        SensorSnapshot = 1,
        DispenseProgress = 2,
        DispenseCompleted = 3,
        Acknowledge = 4,
    }
}
