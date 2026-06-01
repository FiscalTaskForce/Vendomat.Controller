using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Security;

namespace Vendomat.Controller.Cloud.Data;

public sealed class CloudStore(IHostEnvironment environment, IConfiguration configuration)
{
    public sealed class CloudOperationalHealth
    {
        public string Status { get; set; } = "online";
        public DateTimeOffset TimestampUtc { get; set; }
        public int MachineCount { get; set; }
        public int CompanionSessionCount { get; set; }
        public int PendingCommandCount { get; set; }
        public int SchemaVersion { get; set; }
    }

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly ConcurrentDictionary<Guid, PendingPairingSecret> _pendingPairingSecrets = new();
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
                         CREATE TABLE IF NOT EXISTS schema_migrations (
                             Version INTEGER PRIMARY KEY,
                             Name TEXT NOT NULL,
                             AppliedAtUtc TEXT NOT NULL
                         );
                         """,
                         """
                         CREATE TABLE IF NOT EXISTS machines (
                             MachineId TEXT PRIMARY KEY,
                             MachineName TEXT NOT NULL,
                             MachineToken TEXT NOT NULL,
                             MachineTokenPrefix TEXT NOT NULL DEFAULT '',
                             CompanionAccessToken TEXT NOT NULL DEFAULT '',
                             CompanionAccessTokenPrefix TEXT NOT NULL DEFAULT '',
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
                             CompanionAccessTokenPrefix TEXT NOT NULL DEFAULT '',
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

            await ApplyMigrationAsync(connection, 1, "machines_last_dashboard_json", () =>
                EnsureTextColumnAsync(connection, "machines", "LastDashboardJson", cancellationToken), cancellationToken);
            await ApplyMigrationAsync(connection, 2, "machines_machine_token_prefix", () =>
                EnsureTextColumnAsync(connection, "machines", "MachineTokenPrefix", cancellationToken), cancellationToken);
            await ApplyMigrationAsync(connection, 3, "machines_companion_token_prefix", () =>
                EnsureTextColumnAsync(connection, "machines", "CompanionAccessTokenPrefix", cancellationToken), cancellationToken);
            await ApplyMigrationAsync(connection, 4, "companion_sessions_companion_token_prefix", () =>
                EnsureTextColumnAsync(connection, "companion_sessions", "CompanionAccessTokenPrefix", cancellationToken), cancellationToken);
            await ApplyMigrationAsync(connection, 5, "hash_existing_machine_secrets", () =>
                HashExistingMachineSecretsAsync(connection, cancellationToken), cancellationToken);
            await ApplyMigrationAsync(connection, 6, "hash_existing_companion_secrets", () =>
                HashExistingCompanionSecretsAsync(connection, cancellationToken), cancellationToken);
            await ApplyMigrationAsync(connection, 7, "commands_source_token_prefix", () =>
                EnsureTextColumnAsync(connection, "commands", "SourceTokenPrefix", cancellationToken), cancellationToken);
            await ApplyMigrationAsync(connection, 8, "commands_payload_hash", () =>
                EnsureTextColumnAsync(connection, "commands", "PayloadHash", cancellationToken), cancellationToken);
            await ApplyMigrationAsync(connection, 9, "commands_status_message", () =>
                EnsureTextColumnAsync(connection, "commands", "StatusMessage", cancellationToken), cancellationToken);

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

        RegisterPendingPairingSecret(request.MachineId, request.CompanionAccessToken, request.ExpiresAtUtc);

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
        var snapshot = SanitizeSnapshotForStorage(request.Snapshot ?? new MachineStatusSnapshot());
        var dashboard = SanitizeDashboardForStorage(request.Dashboard);

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
            dashboard is null ? null : JsonSerializer.Serialize(dashboard, _jsonOptions),
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
        var snapshot = SanitizeSnapshotForStorage(request.Snapshot ?? new MachineStatusSnapshot());
        var dashboard = SanitizeDashboardForStorage(request.Dashboard);

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
            dashboard is null ? null : JsonSerializer.Serialize(dashboard, _jsonOptions),
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
                ErrorMessage = @ErrorMessage,
                StatusMessage = @StatusMessage
            WHERE CommandId = @CommandId
              AND MachineId = @MachineId;
            """;
        command.Parameters.AddWithValue("@Status", request.Success ? "completed" : "failed");
        command.Parameters.AddWithValue("@CompletedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@ErrorMessage", request.ErrorMessage?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@StatusMessage", request.Success ? "executed" : request.ErrorMessage?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@CommandId", request.CommandId.ToString("N"));
        command.Parameters.AddWithValue("@MachineId", request.MachineId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ValidateMachineConnectionAsync(Guid machineId, string machineToken, CancellationToken cancellationToken = default)
    {
        ValidateMachineRequest(machineId, machineToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureMachineTokenAsync(connection, transaction, machineId, machineToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CloudOperationalHealth> GetOperationalHealthAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        async Task<int> CountAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(result ?? 0);
        }

        return new CloudOperationalHealth
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            MachineCount = await CountAsync("SELECT COUNT(*) FROM machines;"),
            CompanionSessionCount = await CountAsync("SELECT COUNT(*) FROM companion_sessions;"),
            PendingCommandCount = await CountAsync("SELECT COUNT(*) FROM commands WHERE Status = 'pending' OR Status = 'dispatched';"),
            SchemaVersion = await CountAsync("SELECT COALESCE(MAX(Version), 0) FROM schema_migrations;"),
        };
    }

    public async Task<List<CloudCommandEnvelope>> GetPendingCommandsAsync(
        Guid machineId,
        string machineToken,
        CancellationToken cancellationToken = default)
    {
        ValidateMachineRequest(machineId, machineToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureMachineTokenAsync(connection, transaction, machineId, machineToken, cancellationToken);
        await DeleteOldCompletedCommandsAsync(connection, transaction, cancellationToken);
        var commands = await LoadPendingCommandsAsync(connection, transaction, machineId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return commands;
    }

    public async Task<CloudCompanionSession> ResolveCompanionSessionAsync(
        string companionAccessToken,
        CancellationToken cancellationToken = default)
    {
        var normalizedToken = CompanionAccessTokenSecurity.Normalize(companionAccessToken);
        var machine = await ResolveMachineByCompanionTokenAsync(normalizedToken, cancellationToken);
        return new CloudCompanionSession(
            machine.MachineId,
            machine.MachineName,
            machine.CloudApiBaseUrl,
            normalizedToken);
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
                m.CompanionAccessTokenPrefix,
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

        var machineId = ParseGuid(reader.GetString(reader.GetOrdinal("MachineId")));
        var storedCompanionAccessToken = reader.GetString(reader.GetOrdinal("CompanionAccessToken"));
        var rawCompanionAccessToken = ResolvePendingPairingSecret(machineId, storedCompanionAccessToken);

        var result = new PairingClaimResult
        {
            MachineId = machineId,
            MachineName = reader.GetString(reader.GetOrdinal("MachineName")),
            LocalApiBaseUrl = reader.GetString(reader.GetOrdinal("LocalApiBaseUrl")),
            PublicApiBaseUrl = reader.GetString(reader.GetOrdinal("PublicApiBaseUrl")),
            CloudApiBaseUrl = reader.GetString(reader.GetOrdinal("CloudApiBaseUrl")),
            CompanionAccessToken = rawCompanionAccessToken,
            IssuedAtUtc = DateTimeOffset.UtcNow,
        };

        if (string.IsNullOrWhiteSpace(result.CompanionAccessToken))
        {
            throw new InvalidOperationException("The machine has not synced a companion token yet.");
        }

        await reader.DisposeAsync();

        await using (var sessionCommand = connection.CreateCommand())
        {
            var sessionTokenHash = CompanionAccessTokenSecurity.HashForStorage(result.CompanionAccessToken);
            var sessionTokenPrefix = CompanionAccessTokenSecurity.GetAuditPrefix(sessionTokenHash);
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText =
                """
                INSERT INTO companion_sessions (CompanionAccessToken, CompanionAccessTokenPrefix, MachineId, IssuedAtUtc, LastUsedUtc)
                VALUES (@CompanionAccessToken, @CompanionAccessTokenPrefix, @MachineId, @IssuedAtUtc, @LastUsedUtc)
                ON CONFLICT(CompanionAccessToken) DO UPDATE SET
                    CompanionAccessTokenPrefix = excluded.CompanionAccessTokenPrefix,
                    MachineId = excluded.MachineId,
                    LastUsedUtc = excluded.LastUsedUtc;
                """;
            sessionCommand.Parameters.AddWithValue("@CompanionAccessToken", sessionTokenHash);
            sessionCommand.Parameters.AddWithValue("@CompanionAccessTokenPrefix", sessionTokenPrefix);
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
        _pendingPairingSecrets.TryRemove(result.MachineId, out _);
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

        var snapshot = JsonSerializer.Deserialize<MachineStatusSnapshot>(machine.LastStatusJson, _jsonOptions)
            ?? throw new InvalidOperationException("The cached machine status is invalid.");
        return MachineSnapshotSanitizer.ForExternalApi(snapshot);
    }

    public async Task<MachineSettings> GetSettingsAsync(string companionAccessToken, CancellationToken cancellationToken = default)
    {
        var machine = await ResolveMachineByCompanionTokenAsync(companionAccessToken, cancellationToken);
        if (!string.IsNullOrWhiteSpace(machine.LastSettingsJson))
        {
            var settings = JsonSerializer.Deserialize<MachineSettings>(machine.LastSettingsJson, _jsonOptions);
            if (settings is not null)
            {
                return MachineSnapshotSanitizer.ForExternalApi(settings);
            }
        }

        if (!string.IsNullOrWhiteSpace(machine.LastStatusJson))
        {
            var snapshot = JsonSerializer.Deserialize<MachineStatusSnapshot>(machine.LastStatusJson, _jsonOptions);
            if (snapshot is not null)
            {
                return MachineSnapshotSanitizer.ForExternalApi(snapshot.Settings);
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

        var dashboard = JsonSerializer.Deserialize<MachineDashboardSnapshot>(machine.LastDashboardJson, _jsonOptions)
            ?? throw new InvalidOperationException("The cached machine dashboard is invalid.");
        return MachineSnapshotSanitizer.ForExternalApi(dashboard);
    }

    public async Task<MachineSettings> QueueSettingsAsync(string companionAccessToken, MachineSettings settings, CancellationToken cancellationToken = default)
    {
        var machine = await ResolveMachineByCompanionTokenAsync(companionAccessToken, cancellationToken);
        settings.MachineId = machine.MachineId;
        settings.CloudApiBaseUrl = machine.CloudApiBaseUrl;
        settings.CloudMachineToken = string.Empty;
        settings.CompanionAccessToken = string.Empty;
        await InsertCommandAsync(
            machine.MachineId,
            CloudCommandTypes.SaveSettings,
            JsonSerializer.Serialize(settings, _jsonOptions),
            CompanionAccessTokenSecurity.GetAuditPrefix(companionAccessToken),
            cancellationToken);
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

        return await InsertCommandAsync(
            machine.MachineId,
            CloudCommandTypes.RunSanitation,
            JsonSerializer.Serialize(payload, _jsonOptions),
            CompanionAccessTokenSecurity.GetAuditPrefix(companionAccessToken),
            cancellationToken);
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

        return await InsertCommandAsync(
            machine.MachineId,
            CloudCommandTypes.AddCredit,
            JsonSerializer.Serialize(payload, _jsonOptions),
            CompanionAccessTokenSecurity.GetAuditPrefix(companionAccessToken),
            cancellationToken);
    }

    public async Task<IReadOnlyList<CloudCompanionSessionInfo>> GetCompanionSessionsAsync(
        Guid machineId,
        string machineToken,
        CancellationToken cancellationToken = default)
    {
        ValidateMachineRequest(machineId, machineToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureMachineTokenAsync(connection, transaction, machineId, machineToken, cancellationToken);

        var result = new List<CloudCompanionSessionInfo>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT CompanionAccessTokenPrefix, IssuedAtUtc, LastUsedUtc
            FROM companion_sessions
            WHERE MachineId = @MachineId
            ORDER BY LastUsedUtc DESC, IssuedAtUtc DESC;
            """;
        command.Parameters.AddWithValue("@MachineId", machineId.ToString("N"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CloudCompanionSessionInfo
            {
                CompanionTokenPrefix = reader.GetString(reader.GetOrdinal("CompanionAccessTokenPrefix")),
                IssuedAtUtc = ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("IssuedAtUtc"))),
                LastUsedUtc = ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("LastUsedUtc"))),
            });
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<int> RevokeCompanionSessionsAsync(
        Guid machineId,
        string machineToken,
        string? companionTokenPrefix,
        CancellationToken cancellationToken = default)
    {
        ValidateMachineRequest(machineId, machineToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await EnsureMachineTokenAsync(connection, transaction, machineId, machineToken, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.IsNullOrWhiteSpace(companionTokenPrefix)
            ? "DELETE FROM companion_sessions WHERE MachineId = @MachineId;"
            : """
              DELETE FROM companion_sessions
              WHERE MachineId = @MachineId
                AND CompanionAccessTokenPrefix = @CompanionAccessTokenPrefix;
              """;
        command.Parameters.AddWithValue("@MachineId", machineId.ToString("N"));
        if (!string.IsNullOrWhiteSpace(companionTokenPrefix))
        {
            command.Parameters.AddWithValue("@CompanionAccessTokenPrefix", companionTokenPrefix.Trim());
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected;
    }

    private async Task<Guid> InsertCommandAsync(
        Guid machineId,
        string commandType,
        string payloadJson,
        string sourceTokenPrefix,
        CancellationToken cancellationToken)
    {
        var commandId = Guid.NewGuid();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO commands (CommandId, MachineId, CommandType, PayloadJson, Status, CreatedAtUtc, SourceTokenPrefix, PayloadHash, StatusMessage)
            VALUES (@CommandId, @MachineId, @CommandType, @PayloadJson, 'pending', @CreatedAtUtc, @SourceTokenPrefix, @PayloadHash, @StatusMessage);
            """;
        command.Parameters.AddWithValue("@CommandId", commandId.ToString("N"));
        command.Parameters.AddWithValue("@MachineId", machineId.ToString("N"));
        command.Parameters.AddWithValue("@CommandType", commandType);
        command.Parameters.AddWithValue("@PayloadJson", payloadJson);
        command.Parameters.AddWithValue("@CreatedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("@SourceTokenPrefix", sourceTokenPrefix?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@PayloadHash", ComputePayloadHash(payloadJson));
        command.Parameters.AddWithValue("@StatusMessage", "queued");
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
                    DispatchedAtUtc = @DispatchedAtUtc,
                    StatusMessage = 'dispatched'
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

        var tokenPrefix = CompanionAccessTokenSecurity.GetAuditPrefix(normalizedToken);

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
                m.LastDashboardJson,
                c.CompanionAccessToken
            FROM companion_sessions c
            INNER JOIN machines m ON m.MachineId = c.MachineId
            WHERE c.CompanionAccessTokenPrefix = @CompanionAccessTokenPrefix
               OR c.CompanionAccessToken = @LegacyCompanionAccessToken;
            """;
        command.Parameters.AddWithValue("@CompanionAccessTokenPrefix", tokenPrefix);
        command.Parameters.AddWithValue("@LegacyCompanionAccessToken", normalizedToken);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        ResolvedMachine? machine = null;
        var storedSessionToken = string.Empty;
        while (await reader.ReadAsync(cancellationToken))
        {
            var candidateToken = reader.GetString(reader.GetOrdinal("CompanionAccessToken"));
            if (!CompanionAccessTokenSecurity.Verify(candidateToken, normalizedToken))
            {
                continue;
            }

            storedSessionToken = candidateToken;
            machine = new ResolvedMachine(
                ParseGuid(reader.GetString(reader.GetOrdinal("MachineId"))),
                reader.GetString(reader.GetOrdinal("MachineName")),
                reader.GetString(reader.GetOrdinal("LocalApiBaseUrl")),
                reader.GetString(reader.GetOrdinal("PublicApiBaseUrl")),
                reader.GetString(reader.GetOrdinal("CloudApiBaseUrl")),
                reader.GetString(reader.GetOrdinal("LastStatusJson")),
                reader.GetString(reader.GetOrdinal("LastSettingsJson")),
                reader.GetString(reader.GetOrdinal("LastDashboardJson")));
            break;
        }

        if (machine is null)
        {
            throw new InvalidOperationException("The companion token is not authorized.");
        }

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
        updateCommand.Parameters.AddWithValue("@CompanionAccessToken", storedSessionToken);
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
                MachineId, MachineName, MachineToken, MachineTokenPrefix, CompanionAccessToken, CompanionAccessTokenPrefix, LocalApiBaseUrl, PublicApiBaseUrl, CloudApiBaseUrl,
                LastStatusJson, LastSettingsJson, LastDashboardJson, LastSeenUtc, UpdatedAtUtc)
            VALUES (
                @MachineId, @MachineName, @MachineToken, @MachineTokenPrefix, @CompanionAccessToken, @CompanionAccessTokenPrefix, @LocalApiBaseUrl, @PublicApiBaseUrl, @CloudApiBaseUrl,
                @LastStatusJson, @LastSettingsJson, @LastDashboardJson, @LastSeenUtc, @UpdatedAtUtc)
            ON CONFLICT(MachineId) DO UPDATE SET
                MachineName = excluded.MachineName,
                MachineToken = excluded.MachineToken,
                MachineTokenPrefix = excluded.MachineTokenPrefix,
                CompanionAccessToken = CASE WHEN TRIM(excluded.CompanionAccessToken) = '' THEN machines.CompanionAccessToken ELSE excluded.CompanionAccessToken END,
                CompanionAccessTokenPrefix = CASE WHEN TRIM(excluded.CompanionAccessTokenPrefix) = '' THEN machines.CompanionAccessTokenPrefix ELSE excluded.CompanionAccessTokenPrefix END,
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
        var machineTokenHash = CompanionAccessTokenSecurity.HashForStorage(machineToken);
        var companionAccessTokenHash = CompanionAccessTokenSecurity.HashForStorage(companionAccessToken);
        command.Parameters.AddWithValue("@MachineToken", machineTokenHash);
        command.Parameters.AddWithValue("@MachineTokenPrefix", CompanionAccessTokenSecurity.GetAuditPrefix(machineTokenHash));
        command.Parameters.AddWithValue("@CompanionAccessToken", companionAccessTokenHash);
        command.Parameters.AddWithValue("@CompanionAccessTokenPrefix", CompanionAccessTokenSecurity.GetAuditPrefix(companionAccessTokenHash));
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

    private void RegisterPendingPairingSecret(Guid machineId, string companionAccessToken, DateTimeOffset expiresAtUtc)
    {
        var normalizedToken = CompanionAccessTokenSecurity.Normalize(companionAccessToken);
        if (machineId == Guid.Empty || string.IsNullOrWhiteSpace(normalizedToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var item in _pendingPairingSecrets.Where(item => item.Value.ExpiresAtUtc <= now).ToList())
        {
            _pendingPairingSecrets.TryRemove(item.Key, out _);
        }

        _pendingPairingSecrets[machineId] = new PendingPairingSecret(normalizedToken, expiresAtUtc.ToUniversalTime());
    }

    private string ResolvePendingPairingSecret(Guid machineId, string storedCompanionAccessToken)
    {
        if (_pendingPairingSecrets.TryGetValue(machineId, out var pending)
            && pending.ExpiresAtUtc > DateTimeOffset.UtcNow
            && CompanionAccessTokenSecurity.Verify(storedCompanionAccessToken, pending.CompanionAccessToken))
        {
            return pending.CompanionAccessToken;
        }

        if (!CompanionAccessTokenSecurity.IsStoredHash(storedCompanionAccessToken))
        {
            return CompanionAccessTokenSecurity.Normalize(storedCompanionAccessToken);
        }

        throw new InvalidOperationException("The active pairing token is no longer available. Generate a new QR code.");
    }

    private MachineStatusSnapshot SanitizeSnapshotForStorage(MachineStatusSnapshot snapshot)
    {
        var clone = JsonSerializer.Deserialize<MachineStatusSnapshot>(JsonSerializer.Serialize(snapshot, _jsonOptions), _jsonOptions)
            ?? new MachineStatusSnapshot();
        SanitizeSettingsForStorage(clone.Settings);
        return clone;
    }

    private MachineDashboardSnapshot? SanitizeDashboardForStorage(MachineDashboardSnapshot? dashboard)
    {
        if (dashboard is null)
        {
            return null;
        }

        var clone = JsonSerializer.Deserialize<MachineDashboardSnapshot>(JsonSerializer.Serialize(dashboard, _jsonOptions), _jsonOptions);
        if (clone is not null)
        {
            SanitizeSettingsForStorage(clone.Status.Settings);
        }

        return clone;
    }

    private static void SanitizeSettingsForStorage(MachineSettings settings)
    {
        MachineSnapshotSanitizer.SanitizeSettings(settings);
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

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        int version,
        string name,
        Func<Task> applyAsync,
        CancellationToken cancellationToken)
    {
        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE Version = @Version;";
        existsCommand.Parameters.AddWithValue("@Version", version);
        var exists = Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken));
        if (exists > 0)
        {
            return;
        }

        await applyAsync();

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText =
            "INSERT INTO schema_migrations (Version, Name, AppliedAtUtc) VALUES (@Version, @Name, @AppliedAtUtc);";
        insertCommand.Parameters.AddWithValue("@Version", version);
        insertCommand.Parameters.AddWithValue("@Name", name);
        insertCommand.Parameters.AddWithValue("@AppliedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task HashExistingMachineSecretsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<StoredMachineSecrets>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT MachineId, MachineToken, CompanionAccessToken FROM machines;";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new StoredMachineSecrets(
                    reader.GetString(reader.GetOrdinal("MachineId")),
                    reader.GetString(reader.GetOrdinal("MachineToken")),
                    reader.GetString(reader.GetOrdinal("CompanionAccessToken"))));
            }
        }

        foreach (var row in rows)
        {
            var machineTokenHash = CompanionAccessTokenSecurity.HashForStorage(row.MachineToken);
            var companionTokenHash = CompanionAccessTokenSecurity.HashForStorage(row.CompanionAccessToken);
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE machines
                SET MachineToken = @MachineToken,
                    MachineTokenPrefix = @MachineTokenPrefix,
                    CompanionAccessToken = @CompanionAccessToken,
                    CompanionAccessTokenPrefix = @CompanionAccessTokenPrefix
                WHERE MachineId = @MachineId;
                """;
            update.Parameters.AddWithValue("@MachineToken", machineTokenHash);
            update.Parameters.AddWithValue("@MachineTokenPrefix", CompanionAccessTokenSecurity.GetAuditPrefix(machineTokenHash));
            update.Parameters.AddWithValue("@CompanionAccessToken", companionTokenHash);
            update.Parameters.AddWithValue("@CompanionAccessTokenPrefix", CompanionAccessTokenSecurity.GetAuditPrefix(companionTokenHash));
            update.Parameters.AddWithValue("@MachineId", row.MachineId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task HashExistingCompanionSecretsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tokens = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT CompanionAccessToken FROM companion_sessions;";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tokens.Add(reader.GetString(reader.GetOrdinal("CompanionAccessToken")));
            }
        }

        foreach (var token in tokens)
        {
            var tokenHash = CompanionAccessTokenSecurity.HashForStorage(token);
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE companion_sessions
                SET CompanionAccessToken = @CompanionAccessTokenHash,
                    CompanionAccessTokenPrefix = @CompanionAccessTokenPrefix
                WHERE CompanionAccessToken = @CompanionAccessToken;
                """;
            update.Parameters.AddWithValue("@CompanionAccessTokenHash", tokenHash);
            update.Parameters.AddWithValue("@CompanionAccessTokenPrefix", CompanionAccessTokenSecurity.GetAuditPrefix(tokenHash));
            update.Parameters.AddWithValue("@CompanionAccessToken", token);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
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

    private static string ComputePayloadHash(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return string.Empty;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
    }

    public sealed record CloudCompanionSession(
        Guid MachineId,
        string MachineName,
        string CloudApiBaseUrl,
        string CompanionAccessToken);

    private sealed record ResolvedMachine(
        Guid MachineId,
        string MachineName,
        string LocalApiBaseUrl,
        string PublicApiBaseUrl,
        string CloudApiBaseUrl,
        string LastStatusJson,
        string LastSettingsJson,
        string LastDashboardJson);

    private sealed record PendingPairingSecret(string CompanionAccessToken, DateTimeOffset ExpiresAtUtc);

    private sealed record StoredMachineSecrets(string MachineId, string MachineToken, string CompanionAccessToken);
}
