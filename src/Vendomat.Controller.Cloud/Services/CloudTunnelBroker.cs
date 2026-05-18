using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Cloud.Data;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Cloud.Services;

public sealed class CloudTunnelBroker(CloudStore store)
{
    private static readonly TimeSpan MachineRequestTimeout = TimeSpan.FromSeconds(20);

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly ConcurrentDictionary<Guid, MachineConnection> _machineConnections = new();

    public async Task RunMachineSessionAsync(
        WebSocket socket,
        Guid machineId,
        string machineToken,
        CancellationToken cancellationToken = default)
    {
        await store.ValidateMachineConnectionAsync(machineId, machineToken, cancellationToken);

        var connection = new MachineConnection(machineId, socket, _jsonOptions);
        if (_machineConnections.TryGetValue(machineId, out var existing))
        {
            await existing.CloseAsync("Replaced by a newer machine session.", cancellationToken);
        }

        _machineConnections[machineId] = connection;

        try
        {
            var receiveTask = connection.ReceiveLoopAsync(async envelope =>
            {
                switch (envelope.MessageType)
                {
                    case CloudTunnelMessageTypes.Sync:
                        if (!string.Equals(envelope.Action, CloudTunnelActions.SyncState, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("Unknown machine sync action.");
                        }

                        var syncRequest = DeserializePayload<CloudMachineSyncRequest>(envelope.Payload);
                        syncRequest.MachineId = machineId;
                        syncRequest.MachineToken = machineToken;
                        await store.UpdateMachineSnapshotAsync(syncRequest, cancellationToken);
                        break;

                    case CloudTunnelMessageTypes.Response:
                        connection.CompletePendingRequest(envelope);
                        break;

                    case CloudTunnelMessageTypes.Ping:
                        await connection.SendAsync(new CloudTunnelEnvelope
                        {
                            MessageType = CloudTunnelMessageTypes.Pong,
                            MachineId = machineId,
                        }, cancellationToken);
                        break;
                }
            }, cancellationToken);

            await DispatchPendingCommandsAsync(connection, machineToken, cancellationToken);
            await receiveTask;
        }
        finally
        {
            if (_machineConnections.TryGetValue(machineId, out var current)
                && ReferenceEquals(current, connection))
            {
                _machineConnections.TryRemove(machineId, out _);
            }

            connection.FailPendingRequests(new InvalidOperationException("Machine tunnel disconnected."));
            await connection.DisposeAsync();
        }
    }

    public async Task RunCompanionSessionAsync(
        WebSocket socket,
        string companionAccessToken,
        CancellationToken cancellationToken = default)
    {
        var companionSession = await store.ResolveCompanionSessionAsync(companionAccessToken, cancellationToken);

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            CloudTunnelEnvelope request;

            try
            {
                request = await ReceiveAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (request.MessageType == CloudTunnelMessageTypes.Ping)
            {
                await SendAsync(socket, new CloudTunnelEnvelope
                {
                    MessageType = CloudTunnelMessageTypes.Pong,
                    MachineId = companionSession.MachineId,
                }, cancellationToken);
                continue;
            }

            if (!string.Equals(request.MessageType, CloudTunnelMessageTypes.Request, StringComparison.Ordinal))
            {
                continue;
            }

            request.MachineId = companionSession.MachineId;

            CloudTunnelEnvelope response;
            try
            {
                response = await ForwardOrHandleOfflineAsync(companionSession, request, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                response = BuildError(request, ex.Message);
            }

            await SendAsync(socket, response, cancellationToken);
        }

        await CloseIfOpenAsync(socket, "Companion session ended.", cancellationToken);
    }

    private async Task<CloudTunnelEnvelope> ForwardOrHandleOfflineAsync(
        CloudStore.CloudCompanionSession companionSession,
        CloudTunnelEnvelope request,
        CancellationToken cancellationToken)
    {
        if (_machineConnections.TryGetValue(companionSession.MachineId, out var connection))
        {
            return await connection.SendRequestAsync(request, MachineRequestTimeout, cancellationToken);
        }

        return request.Action switch
        {
            CloudTunnelActions.GetStatus => BuildSuccess(
                request,
                await store.GetStatusAsync(companionSession.CompanionAccessToken, cancellationToken)),

            CloudTunnelActions.GetDashboard => BuildSuccess(
                request,
                await store.GetDashboardAsync(companionSession.CompanionAccessToken, cancellationToken)),

            CloudTunnelActions.GetSettings => BuildSuccess(
                request,
                await store.GetSettingsAsync(companionSession.CompanionAccessToken, cancellationToken)),

            CloudTunnelActions.SaveSettings => BuildSuccess(
                request,
                await store.QueueSettingsAsync(
                    companionSession.CompanionAccessToken,
                    DeserializePayload<MachineSettings>(request.Payload),
                    cancellationToken)),

            CloudTunnelActions.RunSanitation => BuildSuccess(
                request,
                new CloudTunnelAcceptedResponse
                {
                    Status = (await QueueSanitationAsync(companionSession, request.Payload, cancellationToken)).Status,
                }),

            CloudTunnelActions.AddCredit => BuildSuccess(
                request,
                await QueueCreditAsync(companionSession, request.Payload, cancellationToken)),

            _ => BuildError(request, $"Unsupported tunnel action '{request.Action}'."),
        };
    }

    private async Task<CloudTunnelAcceptedResponse> QueueSanitationAsync(
        CloudStore.CloudCompanionSession companionSession,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var sanitationRequest = DeserializePayload<SanitationRequest>(payload);
        await store.QueueSanitationAsync(companionSession.CompanionAccessToken, sanitationRequest, cancellationToken);
        return new CloudTunnelAcceptedResponse();
    }

    private async Task<CloudTunnelRemoteCreditResponse> QueueCreditAsync(
        CloudStore.CloudCompanionSession companionSession,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var creditRequest = DeserializePayload<RemoteCreditRequest>(payload);
        var commandId = await store.QueueCreditAsync(
            companionSession.CompanionAccessToken,
            creditRequest.Amount,
            cancellationToken);

        return new CloudTunnelRemoteCreditResponse
        {
            IsQueued = true,
            CommandId = commandId,
        };
    }

    private async Task DispatchPendingCommandsAsync(
        MachineConnection connection,
        string machineToken,
        CancellationToken cancellationToken)
    {
        var commands = await store.GetPendingCommandsAsync(connection.MachineId, machineToken, cancellationToken);
        foreach (var command in commands)
        {
            var request = MapPendingCommand(connection.MachineId, command);
            var completion = new CloudCommandCompletionRequest
            {
                MachineId = connection.MachineId,
                MachineToken = machineToken,
                CommandId = command.CommandId,
                Success = true,
            };

            try
            {
                var response = await connection.SendRequestAsync(request, MachineRequestTimeout, cancellationToken);
                completion.Success = response.Success;
                completion.ErrorMessage = response.ErrorMessage;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                completion.Success = false;
                completion.ErrorMessage = ex.Message;
            }

            await store.CompleteCommandAsync(completion, cancellationToken);
        }
    }

    private CloudTunnelEnvelope MapPendingCommand(Guid machineId, CloudCommandEnvelope command)
    {
        return command.CommandType switch
        {
            var action when string.Equals(action, CloudCommandTypes.SaveSettings, StringComparison.Ordinal) && command.Settings is not null =>
                BuildRequest(machineId, command.CommandId.ToString("N"), CloudTunnelActions.SaveSettings, command.Settings),

            var action when string.Equals(action, CloudCommandTypes.RunSanitation, StringComparison.Ordinal) && command.SanitationCommand is not null =>
                BuildRequest(
                    machineId,
                    command.CommandId.ToString("N"),
                    CloudTunnelActions.RunSanitation,
                    new SanitationRequest
                    {
                        Mode = command.SanitationCommand.Mode,
                        Duration = command.SanitationCommand.Duration,
                        PulseOn = command.SanitationCommand.PulseOn,
                        PulseOff = command.SanitationCommand.PulseOff,
                    }),

            var action when string.Equals(action, CloudCommandTypes.AddCredit, StringComparison.Ordinal) && command.CreditCommand is not null =>
                BuildRequest(
                    machineId,
                    command.CommandId.ToString("N"),
                    CloudTunnelActions.AddCredit,
                    new RemoteCreditRequest
                    {
                        Amount = command.CreditCommand.Amount,
                    }),

            _ => throw new InvalidOperationException($"Unsupported queued command type '{command.CommandType}'."),
        };
    }

    private CloudTunnelEnvelope BuildRequest(Guid machineId, string requestId, string action, object payload) =>
        new()
        {
            MessageType = CloudTunnelMessageTypes.Request,
            RequestId = requestId,
            MachineId = machineId,
            Action = action,
            Payload = SerializePayload(payload),
        };

    private CloudTunnelEnvelope BuildSuccess(CloudTunnelEnvelope request, object? payload) =>
        new()
        {
            MessageType = CloudTunnelMessageTypes.Response,
            RequestId = request.RequestId,
            MachineId = request.MachineId,
            Action = request.Action,
            Success = true,
            Payload = SerializePayload(payload),
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

    private T DeserializePayload<T>(JsonElement payload) =>
        payload.Deserialize<T>(_jsonOptions)
        ?? throw new InvalidOperationException("Tunnel payload is invalid.");

    private JsonElement SerializePayload(object? payload) =>
        payload is null
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(payload, _jsonOptions);

    private async Task<CloudTunnelEnvelope> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var json = await ReceiveTextAsync(socket, cancellationToken);
        return JsonSerializer.Deserialize<CloudTunnelEnvelope>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Tunnel message payload is invalid.");
    }

    private async Task SendAsync(WebSocket socket, CloudTunnelEnvelope envelope, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, _jsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(buffer, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("WebSocket connection closed.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task CloseIfOpenAsync(WebSocket socket, string description, CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, description, cancellationToken);
        }
    }

    private sealed class MachineConnection(Guid machineId, WebSocket socket, JsonSerializerOptions jsonOptions) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CloudTunnelEnvelope>> _pendingRequests = new(StringComparer.Ordinal);

        public Guid MachineId { get; } = machineId;

        public async Task ReceiveLoopAsync(Func<CloudTunnelEnvelope, Task> onMessage, CancellationToken cancellationToken)
        {
            try
            {
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var json = await ReceiveTextAsync(socket, cancellationToken);
                    var envelope = JsonSerializer.Deserialize<CloudTunnelEnvelope>(json, jsonOptions)
                        ?? throw new InvalidOperationException("Machine tunnel message is invalid.");
                    await onMessage(envelope);
                }
            }
            finally
            {
                FailPendingRequests(new InvalidOperationException("Machine tunnel disconnected."));
            }
        }

        public async Task<CloudTunnelEnvelope> SendRequestAsync(
            CloudTunnelEnvelope request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<CloudTunnelEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingRequests.TryAdd(request.RequestId, completion))
            {
                throw new InvalidOperationException("Tunnel request id is already in use.");
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(timeout);
            using var registration = linkedCts.Token.Register(() => completion.TrySetCanceled(linkedCts.Token));

            try
            {
                await SendAsync(request, linkedCts.Token);
                return await completion.Task;
            }
            finally
            {
                _pendingRequests.TryRemove(request.RequestId, out _);
            }
        }

        public void CompletePendingRequest(CloudTunnelEnvelope response)
        {
            if (_pendingRequests.TryGetValue(response.RequestId, out var completion))
            {
                completion.TrySetResult(response);
            }
        }

        public void FailPendingRequests(Exception exception)
        {
            foreach (var completion in _pendingRequests.Values)
            {
                completion.TrySetException(exception);
            }
        }

        public async Task SendAsync(CloudTunnelEnvelope envelope, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(envelope, jsonOptions);
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

        public async Task CloseAsync(string description, CancellationToken cancellationToken)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, description, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            socket.Dispose();
            _sendLock.Dispose();
            await ValueTask.CompletedTask;
        }
    }
}
