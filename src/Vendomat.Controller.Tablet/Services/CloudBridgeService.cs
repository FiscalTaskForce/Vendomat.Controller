using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Android.Util;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Tablet.Services;

public sealed class CloudBridgeService(
    IMachineRuntimeService machineRuntimeService,
    CloudBrokerClient cloudBrokerClient)
{
    private const string LogTag = "VendomatCloud";
    private static readonly TimeSpan IdleSyncInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BusySyncInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DashboardSyncInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _loopTask;
    private CancellationTokenSource? _loopCts;
    private DateTimeOffset _lastDashboardSyncUtc = DateTimeOffset.MinValue;

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
    }

    public async Task PublishPairingAsync(PairingQrPayload payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payload.CloudApiBaseUrl))
        {
            return;
        }

        var settings = await machineRuntimeService.GetSettingsAsync(cancellationToken);
        var request = new CloudPairingUpsertRequest
        {
            MachineId = settings.MachineId,
            MachineName = settings.MachineName,
            MachineToken = settings.CloudMachineToken,
            CompanionAccessToken = settings.CompanionAccessToken,
            LocalApiBaseUrl = settings.LocalApiBaseUrl,
            PublicApiBaseUrl = settings.PublicApiBaseUrl,
            CloudApiBaseUrl = settings.CloudApiBaseUrl,
            PairingCode = payload.PairingCode,
            ExpiresAtUtc = payload.ExpiresAtUtc,
        };

        await cloudBrokerClient.PublishPairingAsync(request, cancellationToken);
        Log.Info(LogTag, $"Published pairing session for machine {settings.MachineId:N}");
    }

    public Task SyncNowAsync(CancellationToken cancellationToken = default) =>
        PublishSnapshotAsync(forceDashboard: true, cancellationToken);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var settings = await machineRuntimeService.GetSettingsAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(settings.CloudApiBaseUrl) || string.IsNullOrWhiteSpace(settings.CloudMachineToken))
                {
                    await Task.Delay(IdleSyncInterval, cancellationToken);
                    continue;
                }

                using var socket = await ConnectAsync(settings, cancellationToken);
                Log.Info(LogTag, $"Machine tunnel connected for {settings.MachineId:N}");
                _lastDashboardSyncUtc = DateTimeOffset.MinValue;

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var receiveTask = ReceiveLoopAsync(socket, linkedCts.Token);
                var syncTask = RunSyncLoopAsync(socket, settings, linkedCts.Token);

                await Task.WhenAny(receiveTask, syncTask);
                linkedCts.Cancel();

                await AwaitSilentlyAsync(receiveTask);
                await AwaitSilentlyAsync(syncTask);

                await CloseSocketAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Machine tunnel failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunSyncLoopAsync(
        ClientWebSocket socket,
        MachineSettings initialSettings,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var settings = await machineRuntimeService.GetSettingsAsync(cancellationToken);
            if (HasConnectionIdentityChanged(initialSettings, settings))
            {
                return;
            }

            var delay = await PublishSnapshotAsync(socket, settings, forceDashboard: false, cancellationToken);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task<TimeSpan> PublishSnapshotAsync(
        ClientWebSocket socket,
        MachineSettings settings,
        bool forceDashboard,
        CancellationToken cancellationToken)
    {
        var snapshot = await machineRuntimeService.GetStatusAsync(cancellationToken);
        var includeDashboard = forceDashboard
            || DateTimeOffset.UtcNow - _lastDashboardSyncUtc >= DashboardSyncInterval;

        var envelope = new CloudTunnelEnvelope
        {
            MessageType = CloudTunnelMessageTypes.Sync,
            MachineId = settings.MachineId,
            Action = CloudTunnelActions.SyncState,
            Payload = SerializePayload(await BuildSyncRequestAsync(settings, snapshot, includeDashboard, cancellationToken)),
        };

        await SendAsync(socket, envelope, cancellationToken);
        if (includeDashboard)
        {
            _lastDashboardSyncUtc = DateTimeOffset.UtcNow;
        }

        return snapshot.Session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning
            ? BusySyncInterval
            : IdleSyncInterval;
    }

    private async Task PublishSnapshotAsync(bool forceDashboard, CancellationToken cancellationToken)
    {
        var settings = await machineRuntimeService.GetSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.CloudApiBaseUrl) || string.IsNullOrWhiteSpace(settings.CloudMachineToken))
        {
            return;
        }

        using var socket = await ConnectAsync(settings, cancellationToken);
        await PublishSnapshotAsync(socket, settings, forceDashboard, cancellationToken);
        await CloseSocketAsync(socket, cancellationToken);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveAsync(socket, cancellationToken);
            switch (message.MessageType)
            {
                case CloudTunnelMessageTypes.Request:
                    var response = await HandleRequestAsync(message, cancellationToken);
                    await SendAsync(socket, response, cancellationToken);
                    break;

                case CloudTunnelMessageTypes.Ping:
                    await SendAsync(socket, new CloudTunnelEnvelope
                    {
                        MessageType = CloudTunnelMessageTypes.Pong,
                        MachineId = message.MachineId,
                    }, cancellationToken);
                    break;
            }
        }
    }

    private async Task<CloudTunnelEnvelope> HandleRequestAsync(
        CloudTunnelEnvelope request,
        CancellationToken cancellationToken)
    {
        try
        {
            return request.Action switch
            {
                CloudTunnelActions.GetStatus => BuildSuccess(
                    request,
                    await machineRuntimeService.GetStatusAsync(cancellationToken)),

                CloudTunnelActions.GetDashboard => BuildSuccess(
                    request,
                    await machineRuntimeService.GetDashboardAsync(cancellationToken)),

                CloudTunnelActions.GetSettings => BuildSuccess(
                    request,
                    await machineRuntimeService.GetSettingsAsync(cancellationToken)),

                CloudTunnelActions.SaveSettings => BuildSuccess(
                    request,
                    await SaveSettingsAsync(request.Payload, cancellationToken)),

                CloudTunnelActions.RunSanitation => BuildSuccess(
                    request,
                    await RunSanitationAsync(request.Payload, cancellationToken)),

                CloudTunnelActions.AddCredit => BuildSuccess(
                    request,
                    await AddCreditAsync(request.Payload, cancellationToken)),

                _ => BuildError(request, $"Unsupported tunnel action '{request.Action}'."),
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildError(request, ex.Message);
        }
    }

    private async Task<MachineSettings> SaveSettingsAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var settings = DeserializePayload<MachineSettings>(payload);
        await machineRuntimeService.SaveSettingsAsync(settings, cancellationToken);
        return await machineRuntimeService.GetSettingsAsync(cancellationToken);
    }

    private async Task<CloudTunnelAcceptedResponse> RunSanitationAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = DeserializePayload<SanitationRequest>(payload);
        await machineRuntimeService.RunSanitationAsync(request, cancellationToken);
        return new CloudTunnelAcceptedResponse();
    }

    private async Task<CloudTunnelRemoteCreditResponse> AddCreditAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = DeserializePayload<RemoteCreditRequest>(payload);
        await machineRuntimeService.AddRemoteCreditAsync(request.Amount, cancellationToken);

        return new CloudTunnelRemoteCreditResponse
        {
            Snapshot = await machineRuntimeService.GetStatusAsync(cancellationToken),
        };
    }

    private async Task<CloudMachineSyncRequest> BuildSyncRequestAsync(
        MachineSettings settings,
        MachineStatusSnapshot snapshot,
        bool includeDashboard,
        CancellationToken cancellationToken)
    {
        return new CloudMachineSyncRequest
        {
            MachineId = settings.MachineId,
            MachineName = settings.MachineName,
            MachineToken = settings.CloudMachineToken,
            CompanionAccessToken = settings.CompanionAccessToken,
            LocalApiBaseUrl = settings.LocalApiBaseUrl,
            PublicApiBaseUrl = settings.PublicApiBaseUrl,
            CloudApiBaseUrl = settings.CloudApiBaseUrl,
            Snapshot = snapshot,
            Dashboard = includeDashboard
                ? await machineRuntimeService.GetDashboardAsync(cancellationToken)
                : null,
        };
    }

    private async Task<ClientWebSocket> ConnectAsync(MachineSettings settings, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("X-Vendomat-Machine-Id", settings.MachineId.ToString("N"));
        socket.Options.SetRequestHeader("X-Vendomat-Machine-Token", settings.CloudMachineToken.Trim());
        await socket.ConnectAsync(BuildWebSocketUri(settings.CloudApiBaseUrl, "ws/machine"), cancellationToken);
        return socket;
    }

    private async Task SendAsync(ClientWebSocket socket, CloudTunnelEnvelope envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, _jsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(buffer, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<CloudTunnelEnvelope> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var json = await ReceiveTextAsync(socket, cancellationToken);
        return JsonSerializer.Deserialize<CloudTunnelEnvelope>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Machine tunnel message is invalid.");
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Machine tunnel closed.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private T DeserializePayload<T>(JsonElement payload) =>
        payload.Deserialize<T>(_jsonOptions)
        ?? throw new InvalidOperationException("Tunnel payload is invalid.");

    private JsonElement SerializePayload(object? payload) =>
        payload is null
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(payload, _jsonOptions);

    private static bool HasConnectionIdentityChanged(MachineSettings expected, MachineSettings actual) =>
        expected.MachineId != actual.MachineId
        || !string.Equals(NormalizeCloudBaseUrl(expected.CloudApiBaseUrl), NormalizeCloudBaseUrl(actual.CloudApiBaseUrl), StringComparison.OrdinalIgnoreCase)
        || !string.Equals(expected.CloudMachineToken?.Trim(), actual.CloudMachineToken?.Trim(), StringComparison.Ordinal);

    private static string NormalizeCloudBaseUrl(string? value) =>
        value?.Trim().TrimEnd('/') ?? string.Empty;

    private static Uri BuildWebSocketUri(string cloudApiBaseUrl, string relativePath)
    {
        var normalized = cloudApiBaseUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Cloud API base URL is missing.");
        }

        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"https://{normalized}";
        }

        var baseUri = new Uri(normalized.TrimEnd('/') + "/", UriKind.Absolute);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
        };

        return new Uri(builder.Uri, relativePath);
    }

    private static CloudTunnelEnvelope BuildSuccess(CloudTunnelEnvelope request, object? payload) =>
        new()
        {
            MessageType = CloudTunnelMessageTypes.Response,
            RequestId = request.RequestId,
            MachineId = request.MachineId,
            Action = request.Action,
            Success = true,
            Payload = payload is null
                ? JsonSerializer.SerializeToElement(new { })
                : JsonSerializer.SerializeToElement(payload),
        };

    private static CloudTunnelEnvelope BuildError(CloudTunnelEnvelope request, string errorMessage) =>
        new()
        {
            MessageType = CloudTunnelMessageTypes.Response,
            RequestId = request.RequestId,
            MachineId = request.MachineId,
            Action = request.Action,
            Success = false,
            ErrorMessage = errorMessage,
        };

    private static async Task AwaitSilentlyAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static async Task CloseSocketAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Machine tunnel closed.", cancellationToken);
        }
    }
}
