using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Security;

namespace Vendomat.Controller.Cloud.Data;

public sealed class CloudStore(IHostEnvironment environment, IConfiguration configuration)
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly string _connectionString = BuildConnectionString(environment, configuration);
    private bool _isInitialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
            {
                return;
            }

            await using var connection = await OpenConnectionCoreAsync(cancellationToken);
            foreach (var sql in new[]
                     {
                         """
                         CREATE TABLE IF NOT EXISTS machines (
                             MachineId TEXT PRIMARY KEY,
                             MachineName TEXT NOT NULL,
                             MachineToken TEXT NOT NULL,
                             CompanionAccessToken TEXT NOT NULL DEFAULT '',
                             LocalApiBaseUrl TEXT NOT NULL DEFAULT '',
                             PublicApiBaseUrl TEXT NOT NULL DEFAULT '',
                             CloudApiBaseUrl TEXT NOT NULL DEFAULT '',
                             LastStatusJson TEXT NOT NULL DEFAULT '',
                             LastSettingsJson TEXT NOT NULL DEFAULT '',
                             LastDashboardJson TEXT NOT NULL DEFAULT '',
                             LastSeenUtc TEXT NOT NULL DEFAULT '',
                             UpdatedAtUtc TEXT NOT NULL DEFAULT ''
                         );
                         """,
                         """
                         CREATE TABLE IF NOT EXISTS pairing_sessions (
                             MachineId TEXT PRIMARY KEY,
                             PairingCode TEXT NOT NULL,
                             ExpiresAtUtc TEXT NOT NULL,
                             CreatedAtUtc TEXT NOT NULL
                         );
                         """,
                         """
                         CREATE TABLE IF NOT EXISTS companion_sessions (
                             CompanionAccessToken TEXT PRIMARY KEY,
                             MachineId TEXT NOT NULL,
                             IssuedAtUtc TEXT NOT NULL,
                             LastUsedUtc TEXT NOT NULL
                         );
                         """,
                         """
                         CREATE TABLE IF NOT EXISTS commands (
                             CommandId TEXT PRIMARY KEY,
                             MachineId TEXT NOT NULL,
                             CommandType TEXT NOT NULL,
                             PayloadJson TEXT NOT NULL,
                             Status TEXT NOT NULL,
                             CreatedAtUtc TEXT NOT NULL,
                             DispatchedAtUtc TEXT NOT NULL DEFAULT '',
                             CompletedAtUtc TEXT NOT NULL DEFAULT '',
                             ErrorMessage TEXT NOT NULL DEFAULT ''
                         );
                         """,
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureTextColumnAsync(connection, "machines", "LastDashboardJson", cancellationToken);

            _isInitialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task UpsertPairingSessionAsync(CloudPairingUpsertRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMachineRequest(request.MachineId, request.MachineToken);
        if (string.IsNullOrWhiteSpace(request.PairingCode))
        {
            throw new InvalidOperationException("PairingCode is required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await UpsertMachineAsync(
            connection,
            transaction,
            request.MachineId,
            request.MachineName,
            request.MachineToken,
            request.CompanionAccessToken,
            request.LocalApiBaseUrl,
            request.PublicApiBaseUrl,
            request.CloudApiBaseUrl,
            statusJson: null,
            settingsJson: null,
            dashboardJson: null,
            lastSeenUtc: null,
            cancellationToken);

        await DeleteExpiredPairingsAsync(connection, transaction, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO pairing_sessions (MachineId, PairingCode, ExpiresAtUtc, CreatedAtUtc)
            VALUES (@MachineId, @PairingCode, @ExpiresAtUtc, @CreatedAtUtc)
            ON CONFLICT(MachineId) DO UPDATE SET
                PairingCode = excluded.PairingCode,
                ExpiresAtUtc = excluded.ExpiresAtUtc,
                CreatedAtUtc = excluded.CreatedAtUtc;
            """;
        command.Parameters.AddWithValue("@MachineId", request.MachineId.ToString("N"));
        command.Parameters.AddWithValue("@PairingCode", request.PairingCode.Trim());
        command.Parameters.AddWithValue("@ExpiresAtUtc", request.ExpiresAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@CreatedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CloudMachineSyncResult> SyncMachineAsync(CloudMachineSyncRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMachineRequest(request.MachineId, request.MachineToken);
        var snapshot = request.Snapshot ?? new MachineStatusSnapshot();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await UpsertMachineAsync(
            connection,
            transaction,
            request.MachineId,
            request.MachineName,
            request.MachineToken,
            request.CompanionAccessToken,
            request.LocalApiBaseUrl,
            request.PublicApiBaseUrl,
            request.CloudApiBaseUrl,
            JsonSerializer.Serialize(snapshot, _jsonOptions),
            JsonSerializer.Serialize(snapshot.Settings, _jsonOptions),
            request.Dashboard is null ? null : JsonSerializer.Serialize(request.Dashboard, _jsonOptions),
            snapshot.GeneratedAtUtc == default ? DateTimeOffset.UtcNow : snapshot.GeneratedAtUtc,
            cancellationToken);

        await DeleteExpiredPairingsAsync(connection, transaction, cancellationToken);
        await DeleteOldCompletedCommandsAsync(connection, transaction, cancellationToken);
        var commands = await LoadPendingCommandsAsync(connection, transaction, request.MachineId, cancellationToken);
        var hasActiveWatcher = await HasActiveWatcherAsync(connection, transaction, request.MachineId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CloudMachineSyncResult
        {
            PendingCommands = commands,
            ServerTimeUtc = DateTimeOffset.UtcNow,
            HasActiveWatcher = hasActiveWatcher,
        };
    }

    public async Task<bool> UpdateMachineSnapshotAsync(CloudMachineSyncRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMachineRequest(request.MachineId, request.MachineToken);
        var snapshot = request.Snapshot ?? new MachineStatusSnapshot();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await UpsertMachineAsync(
            connection,
            transaction,
            request.MachineId,
            request.MachineName,
            request.MachineToken,
            request.CompanionAccessToken,
            request.LocalApiBaseUrl,
            request.PublicApiBaseUrl,
            request.CloudApiBaseUrl,
            JsonSerializer.Serialize(snapshot, _jsonOptions),
            JsonSerializer.Serialize(snapshot.Settings, _jsonOptions),
            request.Dashboard is null ? null : JsonSerializer.Serialize(request.Dashboard, _jsonOptions),
            snapshot.GeneratedAtUtc == default ? DateTimeOffset.UtcNow : snapshot.GeneratedAtUtc,
            cancellationToken);

        var hasActiveWatcher = await HasActiveWatcherAsync(connection, transaction, request.MachineId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return hasActiveWatcher;
    }

    public async Task CompleteCommandAsync(CloudCommandCompletionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMachineRequest(request.MachineId, request.MachineToken);
        if (request.CommandId == Guid.Empty)
        {
            throw new InvalidOperationException("CommandId is required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureMachineTokenAsync(connection, transaction, request.MachineId, request.MachineToken, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE commands
            SET Status = @Status,
                CompletedAtUtc = @CompletedAtUtc,
                ErrorMessage = @ErrorMessage
            WHERE CommandId = @CommandId
              AND MachineId = @MachineId;
            """;
        command.Parameters.AddWithValue("@Status", request.Success ? "completed" : "failed");
        command.Parameters.AddWithValue("@CompletedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@ErrorMessage", request.ErrorMessage?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@CommandId", request.CommandId.ToString("N"));
        command.Parameters.AddWithValue("@MachineId", request.MachineId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PairingClaimResult> ClaimPairingAsync(
        PairingClaimRequest request,
        string currentCloudApiBaseUrl,
        CancellationToken cancellationToken = default)
    {
        if (request.MachineId == Guid.Empty || string.IsNullOrWhiteSpace(request.PairingCode))
        {
            throw new InvalidOperationException("MachineId and PairingCode are required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await DeleteExpiredPairingsAsync(connection, transaction, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                m.MachineId,
                m.MachineName,
                m.CompanionAccessToken,
                m.LocalApiBaseUrl,
                m.PublicApiBaseUrl,
                m.CloudApiBaseUrl,
                p.PairingCode
            FROM pairing_sessions p
            INNER JOIN machines m ON m.MachineId = p.MachineId
            WHERE p.MachineId = @MachineId;
            """;
        command.Parameters.AddWithValue("@MachineId", request.MachineId.ToString("N"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("No active pairing session exists for this machine.");
        }

        var storedCode = reader.GetString(reader.GetOrdinal("PairingCode"));
        if (!string.Equals(storedCode, request.PairingCode.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The pairing code is invalid.");
        }

        var result = new PairingClaimResult
        {
            MachineId = ParseGuid(reader.GetString(reader.GetOrdinal("MachineId"))),
            MachineName = reader.GetString(reader.GetOrdinal("MachineName")),
            LocalApiBaseUrl = reader.GetString(reader.GetOrdinal("LocalApiBaseUrl")),
            PublicApiBaseUrl = reader.GetString(reader.GetOrdinal("PublicApiBaseUrl")),
            CloudApiBaseUrl = reader.GetString(reader.GetOrdinal("CloudApiBaseUrl")),
            CompanionAccessToken = reader.GetString(reader.GetOrdinal("CompanionAccessToken")),
            IssuedAtUtc = DateTimeOffset.UtcNow,
        };

        if (string.IsNullOrWhiteSpace(result.CompanionAccessToken))
        {
            throw new InvalidOperationException("The machine has not synced a companion token yet.");
        }

        await reader.DisposeAsync();

        await using (var sessionCommand = connection.CreateCommand())
        {
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText =
                """
                INSERT INTO companion_sessions (CompanionAccessToken, MachineId, IssuedAtUtc, LastUsedUtc)
                VALUES (@CompanionAccessToken, @MachineId, @IssuedAtUtc, @LastUsedUtc)
                ON CONFLICT(CompanionAccessToken) DO UPDATE SET
                    MachineId = excluded.MachineId,
                    LastUsedUtc = excluded.LastUsedUtc;
                """;
            sessionCommand.Parameters.AddWithValue("@CompanionAccessToken", result.CompanionAccessToken);
            sessionCommand.Parameters.AddWithValue("@MachineId", result.MachineId.ToString("N"));
            sessionCommand.Parameters.AddWithValue("@IssuedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            sessionCommand.Parameters.AddWithValue("@LastUsedUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM pairing_sessions WHERE MachineId = @MachineId;";
            deleteCommand.Parameters.AddWithValue("@MachineId", result.MachineId.ToString("N"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(result.CloudApiBaseUrl))
        {
            result.CloudApiBaseUrl = currentCloudApiBaseUrl;
        }

        return result;
    }

    public async Task<MachineStatusSnapshot> GetStatusAsync(string companionAccessToken, CancellationToken cancellationToken = default)
    {
        var machine = await ResolveMachineByCompanionTokenAsync(companionAccessToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(machine.LastStatusJson))
        {
            throw new InvalidOperationException("No status snapshot has been synced from the machine yet.");
        }

        return JsonSerializer.Deserialize<MachineStatusSnapshot>(machine.LastStatusJson, _jsonOptions)
            ?? throw new InvalidOperationException("The cached machine status is invalid.");
    }

    public async Task<MachineSettings> GetSettingsAsync(string companionAccessToken, CancellationToken cancellationToken = default)
    {
        var machine = await ResolveMachineByCompanionTokenAsync(companionAccessToken, cancellationToken);
        if (!string.IsNullOrWhiteSpace(machine.LastSettingsJson))
        {
            var settings = JsonSerializer.Deserialize<MachineSettings>(machine.LastSettingsJson, _jsonOptions);
            if (settings is not null)
            {
                return settings;
            }
        }

        if (!string.IsNullOrWhiteSpace(machine.LastStatusJson))
        {
            var snapshot = JsonSerializer.Deserialize<MachineStatusSnapshot>(machine.LastStatusJson, _jsonOptions);
            if (snapshot is not null)
            {
                return snapshot.Settings;
            }
        }

        throw new InvalidOperationException("No settings snapshot has been synced from the machine yet.");
    }

    public async Task<MachineDashboardSnapshot> GetDashboardAsync(string companionAccessToken, CancellationToken cancellationToken = default)
    {
        var machine = await ResolveMachineByCompanionTokenAsync(companionAccessToken, cancellationToken);
        if (string.IsNullOrWhiteSpace(machine.LastDashboardJson))
        {
            throw new InvalidOperationException("No dashboard snapshot has been synced from the machine yet.");
        }

        return JsonSerializer.Deserialize<MachineDashboardSnapshot>(machine.LastDashboardJson, _jsonOptions)
            ?? throw new InvalidOperationException("The cached machine dashboard is invalid.");
    }

    public async Task<MachineSettings> QueueSettingsAsync(string companionAccessToken, MachineSettings settings, CancellationToken cancellationToken = default)
    {
        var machine = await ResolveMachineByCompanionTokenAsync(companionAccessToken, cancellationToken);
        settings.MachineId = machine.MachineId;
        settings.CloudApiBaseUrl = machine.CloudApiBaseUrl;
        await InsertCommandAsync(machine.MachineId, CloudCommandTypes.SaveSettings, JsonSerializer.Serialize(settings, _jsonOptions), cancellationToken);
        return settings;
    }

    public async Task<Guid> QueueSanitationAsync(string companionAccessToken, SanitationRequest sanitationRequest, CancellationToken cancellationToken = default)
    {
        var machine = await ResolveMachineByCompanionTokenAsync(companionAccessToken, cancellationToken);
        var payload = new CloudSanitationCommand
        {
            Mode = sanitationRequest.Mode,
            Duration = sanitationRequest.Duration,
            PulseOn = sanitationRequest.PulseOn,
            PulseOff = sanitationRequest.PulseOff,
        };

        return await InsertCommandAsync(machine.MachineId, CloudCommandTypes.RunSanitation, JsonSerializer.Serialize(payload, _jsonOptions), cancellationToken);
    }

    public async Task<Guid> QueueCreditAsync(string companionAccessToken, decimal amount, CancellationToken cancellationToken = default)
    {
        if (amount < 0)
        {
            throw new InvalidOperationException("Credit amount cannot be negative.");
        }

        var machine = await ResolveMachineByCompanionTokenAsync(companionAccessToken, cancellationToken);
        var payload = new CloudCreditCommand
        {
            Amount = amount,
        };

        return await InsertCommandAsync(machine.MachineId, CloudCommandTypes.AddCredit, JsonSerializer.Serialize(payload, _jsonOptions), cancellationToken);
    }

    private async Task<Guid> InsertCommandAsync(Guid machineId, string commandType, string payloadJson, CancellationToken cancellationToken)
    {
        var commandId = Guid.NewGuid();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO commands (CommandId, MachineId, CommandType, PayloadJson, Status, CreatedAtUtc)
            VALUES (@CommandId, @MachineId, @CommandType, @PayloadJson, 'pending', @CreatedAtUtc);
            """;
        command.Parameters.AddWithValue("@CommandId", commandId.ToString("N"));
        command.Parameters.AddWithValue("@MachineId", machineId.ToString("N"));
        command.Parameters.AddWithValue("@CommandType", commandType);
        command.Parameters.AddWithValue("@PayloadJson", payloadJson);
        command.Parameters.AddWithValue("@CreatedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return commandId;
    }

    private async Task<List<CloudCommandEnvelope>> LoadPendingCommandsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid machineId, CancellationToken cancellationToken)
    {
        var result = new List<CloudCommandEnvelope>();
        var dispatchedIds = new List<string>();
        var resendThresholdUtc = DateTimeOffset.UtcNow.AddMinutes(-2).UtcDateTime.ToString("O");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT CommandId, CommandType, PayloadJson, CreatedAtUtc
            FROM commands
            WHERE MachineId = @MachineId
              AND (
                    Status = 'pending'
                    OR (
                        Status = 'dispatched'
                        AND (DispatchedAtUtc = '' OR DispatchedAtUtc <= @ResendThresholdUtc)
                    )
                )
            ORDER BY CreatedAtUtc
            LIMIT 20;
            """;
        command.Parameters.AddWithValue("@MachineId", machineId.ToString("N"));
        command.Parameters.AddWithValue("@ResendThresholdUtc", resendThresholdUtc);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var commandId = reader.GetString(reader.GetOrdinal("CommandId"));
            var commandType = reader.GetString(reader.GetOrdinal("CommandType"));
            var payloadJson = reader.GetString(reader.GetOrdinal("PayloadJson"));

            var envelope = new CloudCommandEnvelope
            {
                CommandId = ParseGuid(commandId),
                CommandType = commandType,
                CreatedAtUtc = ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("CreatedAtUtc"))),
            };

            switch (commandType)
            {
                case CloudCommandTypes.SaveSettings:
                    envelope.Settings = JsonSerializer.Deserialize<MachineSettings>(payloadJson, _jsonOptions);
                    break;
                case CloudCommandTypes.RunSanitation:
                    envelope.SanitationCommand = JsonSerializer.Deserialize<CloudSanitationCommand>(payloadJson, _jsonOptions);
                    break;
                case CloudCommandTypes.AddCredit:
                    envelope.CreditCommand = JsonSerializer.Deserialize<CloudCreditCommand>(payloadJson, _jsonOptions);
                    break;
            }

            result.Add(envelope);
            dispatchedIds.Add(commandId);
        }

        await reader.DisposeAsync();

        foreach (var commandId in dispatchedIds)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                UPDATE commands
                SET Status = 'dispatched',
                    DispatchedAtUtc = @DispatchedAtUtc
                WHERE CommandId = @CommandId;
                """;
            updateCommand.Parameters.AddWithValue("@DispatchedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            updateCommand.Parameters.AddWithValue("@CommandId", commandId);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return result;
    }

    private async Task<ResolvedMachine> ResolveMachineByCompanionTokenAsync(string companionAccessToken, CancellationToken cancellationToken)
    {
        var normalizedToken = CompanionAccessTokenSecurity.Normalize(companionAccessToken);
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            throw new InvalidOperationException("The companion token is missing.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                m.MachineId,
                m.MachineName,
                m.LocalApiBaseUrl,
                m.PublicApiBaseUrl,
                m.CloudApiBaseUrl,
                m.LastStatusJson,
                m.LastSettingsJson,
                m.LastDashboardJson
            FROM companion_sessions c
            INNER JOIN machines m ON m.MachineId = c.MachineId
            WHERE c.CompanionAccessToken = @CompanionAccessToken;
            """;
        command.Parameters.AddWithValue("@CompanionAccessToken", normalizedToken);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The companion token is not authorized.");
        }

        var machine = new ResolvedMachine(
            ParseGuid(reader.GetString(reader.GetOrdinal("MachineId"))),
            reader.GetString(reader.GetOrdinal("MachineName")),
            reader.GetString(reader.GetOrdinal("LocalApiBaseUrl")),
            reader.GetString(reader.GetOrdinal("PublicApiBaseUrl")),
            reader.GetString(reader.GetOrdinal("CloudApiBaseUrl")),
            reader.GetString(reader.GetOrdinal("LastStatusJson")),
            reader.GetString(reader.GetOrdinal("LastSettingsJson")),
            reader.GetString(reader.GetOrdinal("LastDashboardJson")));

        await reader.DisposeAsync();

        await using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText =
            """
            UPDATE companion_sessions
            SET LastUsedUtc = @LastUsedUtc
            WHERE CompanionAccessToken = @CompanionAccessToken;
            """;
        updateCommand.Parameters.AddWithValue("@LastUsedUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        updateCommand.Parameters.AddWithValue("@CompanionAccessToken", normalizedToken);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return machine;
    }

    private async Task UpsertMachineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid machineId,
        string machineName,
        string machineToken,
        string companionAccessToken,
        string localApiBaseUrl,
        string publicApiBaseUrl,
        string cloudApiBaseUrl,
        string? statusJson,
        string? settingsJson,
        string? dashboardJson,
        DateTimeOffset? lastSeenUtc,
        CancellationToken cancellationToken)
    {
        await EnsureMachineTokenAsync(connection, transaction, machineId, machineToken, cancellationToken, allowCreate: true);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO machines (
                MachineId, MachineName, MachineToken, CompanionAccessToken, LocalApiBaseUrl, PublicApiBaseUrl, CloudApiBaseUrl,
                LastStatusJson, LastSettingsJson, LastDashboardJson, LastSeenUtc, UpdatedAtUtc)
            VALUES (
                @MachineId, @MachineName, @MachineToken, @CompanionAccessToken, @LocalApiBaseUrl, @PublicApiBaseUrl, @CloudApiBaseUrl,
                @LastStatusJson, @LastSettingsJson, @LastDashboardJson, @LastSeenUtc, @UpdatedAtUtc)
            ON CONFLICT(MachineId) DO UPDATE SET
                MachineName = excluded.MachineName,
                MachineToken = excluded.MachineToken,
                CompanionAccessToken = CASE WHEN TRIM(excluded.CompanionAccessToken) = '' THEN machines.CompanionAccessToken ELSE excluded.CompanionAccessToken END,
                LocalApiBaseUrl = excluded.LocalApiBaseUrl,
                PublicApiBaseUrl = excluded.PublicApiBaseUrl,
                CloudApiBaseUrl = excluded.CloudApiBaseUrl,
                LastStatusJson = CASE WHEN TRIM(excluded.LastStatusJson) = '' THEN machines.LastStatusJson ELSE excluded.LastStatusJson END,
                LastSettingsJson = CASE WHEN TRIM(excluded.LastSettingsJson) = '' THEN machines.LastSettingsJson ELSE excluded.LastSettingsJson END,
                LastDashboardJson = CASE WHEN TRIM(excluded.LastDashboardJson) = '' THEN machines.LastDashboardJson ELSE excluded.LastDashboardJson END,
                LastSeenUtc = CASE WHEN TRIM(excluded.LastSeenUtc) = '' THEN machines.LastSeenUtc ELSE excluded.LastSeenUtc END,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("@MachineId", machineId.ToString("N"));
        command.Parameters.AddWithValue("@MachineName", machineName?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@MachineToken", CompanionAccessTokenSecurity.Normalize(machineToken));
        command.Parameters.AddWithValue("@CompanionAccessToken", CompanionAccessTokenSecurity.Normalize(companionAccessToken));
        command.Parameters.AddWithValue("@LocalApiBaseUrl", localApiBaseUrl?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@PublicApiBaseUrl", publicApiBaseUrl?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@CloudApiBaseUrl", cloudApiBaseUrl?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@LastStatusJson", statusJson ?? string.Empty);
        command.Parameters.AddWithValue("@LastSettingsJson", settingsJson ?? string.Empty);
        command.Parameters.AddWithValue("@LastDashboardJson", dashboardJson ?? string.Empty);
        command.Parameters.AddWithValue("@LastSeenUtc", lastSeenUtc?.UtcDateTime.ToString("O") ?? string.Empty);
        command.Parameters.AddWithValue("@UpdatedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureMachineTokenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid machineId,
        string machineToken,
        CancellationToken cancellationToken,
        bool allowCreate = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MachineToken FROM machines WHERE MachineId = @MachineId;";
        command.Parameters.AddWithValue("@MachineId", machineId.ToString("N"));
        var result = await command.ExecuteScalarAsync(cancellationToken);

        if (result is null || result is DBNull)
        {
            if (allowCreate)
            {
                return;
            }

            throw new InvalidOperationException("The machine is not registered in the cloud.");
        }

        var storedToken = Convert.ToString(result) ?? string.Empty;
        if (!CompanionAccessTokenSecurity.Verify(storedToken, machineToken))
        {
            throw new InvalidOperationException("The machine token is invalid.");
        }
    }

    private static void ValidateMachineRequest(Guid machineId, string machineToken)
    {
        if (machineId == Guid.Empty || string.IsNullOrWhiteSpace(machineToken))
        {
            throw new InvalidOperationException("MachineId and MachineToken are required.");
        }
    }

    private async Task DeleteExpiredPairingsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM pairing_sessions WHERE ExpiresAtUtc < @NowUtc;";
        command.Parameters.AddWithValue("@NowUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureTextColumnAsync(SqliteConnection connection, string tableName, string columnName, CancellationToken cancellationToken)
    {
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(reader.GetOrdinal("name")), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} TEXT NOT NULL DEFAULT '';";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteOldCompletedCommandsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM commands
            WHERE (Status = 'completed' OR Status = 'failed')
              AND CompletedAtUtc <> ''
              AND CompletedAtUtc < @ThresholdUtc;
            """;
        command.Parameters.AddWithValue("@ThresholdUtc", DateTimeOffset.UtcNow.AddDays(-30).UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> HasActiveWatcherAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid machineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1
            FROM companion_sessions
            WHERE MachineId = @MachineId
              AND LastUsedUtc >= @ThresholdUtc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@MachineId", machineId.ToString("N"));
        command.Parameters.AddWithValue("@ThresholdUtc", DateTimeOffset.UtcNow.AddSeconds(-20).UtcDateTime.ToString("O"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is not DBNull;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await OpenConnectionCoreAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionCoreAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA journal_mode=WAL;";
        await pragmaCommand.ExecuteScalarAsync(cancellationToken);
        return connection;
    }

    private static string BuildConnectionString(IHostEnvironment environment, IConfiguration configuration)
    {
        var configuredPath = configuration["Cloud:DatabasePath"];
        var databasePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "App_Data", "vendomat-cloud.db")
            : configuredPath.Trim();

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    private static Guid ParseGuid(string value) =>
        Guid.TryParse(value, out var guid)
            ? guid
            : throw new InvalidOperationException("Stored machine identifier is invalid.");

    private static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.TryParse(value, out var dateTimeOffset)
            ? dateTimeOffset
            : DateTimeOffset.UtcNow;

    private sealed record ResolvedMachine(
        Guid MachineId,
        string MachineName,
        string LocalApiBaseUrl,
        string PublicApiBaseUrl,
        string CloudApiBaseUrl,
        string LastStatusJson,
        string LastSettingsJson,
        string LastDashboardJson);
}
