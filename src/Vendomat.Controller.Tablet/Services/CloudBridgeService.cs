using System.Collections.Concurrent;
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
    private static readonly TimeSpan WatcherSyncInterval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CloudCommandCompletionRequest> _completedCommands = new();
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
        await SyncInternalAsync(settings, cancellationToken);

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

    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        var settings = await machineRuntimeService.GetSettingsAsync(cancellationToken);
        await SyncInternalAsync(settings, cancellationToken);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = IdleSyncInterval;

            try
            {
                var settings = await machineRuntimeService.GetSettingsAsync(cancellationToken);
                delay = await SyncInternalAsync(settings, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Cloud sync failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<TimeSpan> SyncInternalAsync(MachineSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.CloudApiBaseUrl) || string.IsNullOrWhiteSpace(settings.CloudMachineToken))
        {
            return IdleSyncInterval;
        }

        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await machineRuntimeService.GetStatusAsync(cancellationToken);
            var includeDashboard = DateTimeOffset.UtcNow - _lastDashboardSyncUtc >= TimeSpan.FromSeconds(10);
            var syncResult = await cloudBrokerClient.SyncMachineAsync(
                await BuildSyncRequestAsync(settings, snapshot, includeDashboard, cancellationToken),
                cancellationToken);
            if (includeDashboard)
            {
                _lastDashboardSyncUtc = DateTimeOffset.UtcNow;
            }

            foreach (var completed in _completedCommands.Values.ToArray())
            {
                try
                {
                    await cloudBrokerClient.CompleteCommandAsync(completed, settings.CloudApiBaseUrl, cancellationToken);
                    _completedCommands.TryRemove(completed.CommandId, out _);
                }
                catch (Exception ex)
                {
                    Log.Warn(LogTag, $"Cloud command completion retry failed for {completed.CommandId}: {ex.Message}");
                }
            }

            var processedCommand = false;
            foreach (var command in syncResult.PendingCommands)
            {
                await ExecuteCommandAsync(command, settings.CloudApiBaseUrl, settings.MachineId, settings.CloudMachineToken, cancellationToken);
                processedCommand = true;
            }

            if (processedCommand)
            {
                var refreshedSettings = await machineRuntimeService.GetSettingsAsync(cancellationToken);
                var refreshedSnapshot = await machineRuntimeService.GetStatusAsync(cancellationToken);
                syncResult.HasActiveWatcher = await cloudBrokerClient.PublishSnapshotAsync(
                    await BuildSyncRequestAsync(refreshedSettings, refreshedSnapshot, includeDashboard: true, cancellationToken),
                    cancellationToken);
                _lastDashboardSyncUtc = DateTimeOffset.UtcNow;
                settings = refreshedSettings;
            }

            if (syncResult.HasActiveWatcher)
            {
                return WatcherSyncInterval;
            }

            return snapshot.Session.ActivityState is MachineActivityState.Dispensing or MachineActivityState.Cleaning
                ? BusySyncInterval
                : IdleSyncInterval;
        }
        finally
        {
            _syncGate.Release();
        }
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

    private async Task ExecuteCommandAsync(
        CloudCommandEnvelope command,
        string cloudApiBaseUrl,
        Guid machineId,
        string machineToken,
        CancellationToken cancellationToken)
    {
        if (_completedCommands.TryGetValue(command.CommandId, out var completed))
        {
            await cloudBrokerClient.CompleteCommandAsync(completed, cloudApiBaseUrl, cancellationToken);
            _completedCommands.TryRemove(command.CommandId, out _);
            return;
        }

        var completion = new CloudCommandCompletionRequest
        {
            MachineId = machineId,
            MachineToken = machineToken,
            CommandId = command.CommandId,
            Success = true,
        };

        try
        {
            switch (command.CommandType)
            {
                case CloudCommandTypes.SaveSettings when command.Settings is not null:
                    await machineRuntimeService.SaveSettingsAsync(command.Settings, cancellationToken);
                    break;

                case CloudCommandTypes.RunSanitation when command.SanitationCommand is not null:
                    await machineRuntimeService.RunSanitationAsync(new SanitationRequest
                    {
                        Mode = command.SanitationCommand.Mode,
                        Duration = command.SanitationCommand.Duration,
                        PulseOn = command.SanitationCommand.PulseOn,
                        PulseOff = command.SanitationCommand.PulseOff,
                    }, cancellationToken);
                    break;

                case CloudCommandTypes.AddCredit when command.CreditCommand is not null:
                    await machineRuntimeService.AddRemoteCreditAsync(command.CreditCommand.Amount, cancellationToken);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown cloud command type '{command.CommandType}'.");
            }
        }
        catch (Exception ex)
        {
            completion.Success = false;
            completion.ErrorMessage = ex.Message;
        }

        _completedCommands[completion.CommandId] = completion;
        await cloudBrokerClient.CompleteCommandAsync(completion, cloudApiBaseUrl, cancellationToken);
        _completedCommands.TryRemove(completion.CommandId, out _);
    }
}
