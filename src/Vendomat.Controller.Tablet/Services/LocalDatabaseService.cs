using Android.Util;
using SQLite;
using Microsoft.Maui.Storage;
using Vendomat.Controller.Domain.Security;
using Vendomat.Controller.Tablet.Persistence.Entities;

namespace Vendomat.Controller.Tablet.Services;

public sealed class LocalDatabaseService
{
    private const string StartupTag = "VendomatStartup";
    private readonly object _initializeLock = new();
    private SQLiteAsyncConnection? _connection;
    private Task? _initializeTask;

    public SQLiteAsyncConnection Connection =>
        _connection ?? throw new InvalidOperationException("Database not initialized.");

    public async Task InitializeAsync()
    {
        if (_initializeTask is Task existingTask)
        {
            await existingTask;
            return;
        }

        Task initializeTask;
        lock (_initializeLock)
        {
            _initializeTask ??= InitializeCoreAsync();
            initializeTask = _initializeTask;
        }

        await initializeTask;
    }

    private async Task InitializeCoreAsync()
    {
        Log.Info(StartupTag, "LocalDatabaseService.InitializeAsync start");
        var databasePath = Path.Combine(FileSystem.AppDataDirectory, "vendomat-controller.db3");
        Log.Info(StartupTag, $"Database path: {databasePath}");
        _connection = new SQLiteAsyncConnection(
            databasePath,
            SQLiteOpenFlags.Create | SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex);

        await EnsureSchemaMigrationsTableAsync();
        Log.Info(StartupTag, "Creating MachineSettings table");
        await _connection.CreateTableAsync<MachineSettingsEntity>();
        Log.Info(StartupTag, "Creating Sales table");
        await _connection.CreateTableAsync<SaleTransactionEntity>();
        Log.Info(StartupTag, "Creating DeviceLog table");
        await _connection.CreateTableAsync<DeviceLogEntryEntity>();
        Log.Info(StartupTag, "Creating Sanitation table");
        await _connection.CreateTableAsync<SanitationRecordEntity>();
        Log.Info(StartupTag, "Creating Advertisement table");
        await _connection.CreateTableAsync<AdvertisementAssetEntity>();
        await ApplySchemaMigrationsAsync();
        Log.Info(StartupTag, "LocalDatabaseService.InitializeAsync complete");
    }

    private async Task EnsureSchemaMigrationsTableAsync()
    {
        await _connection!.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                Version INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                AppliedAtUtc TEXT NOT NULL
            );
            """);
    }

    private async Task ApplySchemaMigrationsAsync()
    {
        await ApplyMigrationAsync(1, "machine_settings_admin_passcode", async () =>
        {
            await EnsureColumnAsync(
                tableName: "machine_settings",
                columnName: nameof(MachineSettingsEntity.AdminPasscodeHash),
                definition: "TEXT NOT NULL DEFAULT ''");
            await _connection!.ExecuteAsync(
                "UPDATE machine_settings SET AdminPasscodeHash = ? WHERE AdminPasscodeHash IS NULL OR TRIM(AdminPasscodeHash) = ''",
                AdminPasscodeHasher.DefaultHash);
        });

        await ApplyMigrationAsync(2, "machine_settings_public_api", () =>
            EnsureColumnAsync("machine_settings", nameof(MachineSettingsEntity.PublicApiBaseUrl), "TEXT NOT NULL DEFAULT ''"));

        await ApplyMigrationAsync(3, "machine_settings_cloud_api", () =>
            EnsureColumnAsync("machine_settings", nameof(MachineSettingsEntity.CloudApiBaseUrl), "TEXT NOT NULL DEFAULT ''"));

        await ApplyMigrationAsync(4, "machine_settings_cloud_token", () =>
            EnsureColumnAsync("machine_settings", nameof(MachineSettingsEntity.CloudMachineToken), "TEXT NOT NULL DEFAULT ''"));

        await ApplyMigrationAsync(5, "machine_settings_companion_token", () =>
            EnsureColumnAsync("machine_settings", nameof(MachineSettingsEntity.CompanionAccessToken), "TEXT NOT NULL DEFAULT ''"));

        await ApplyMigrationAsync(6, "machine_settings_runtime_mode", async () =>
        {
            await EnsureColumnAsync(
                tableName: "machine_settings",
                columnName: nameof(MachineSettingsEntity.RuntimeMode),
                definition: "TEXT NOT NULL DEFAULT 'Production'");
            await _connection!.ExecuteAsync(
                "UPDATE machine_settings SET RuntimeMode = 'Production' WHERE RuntimeMode IS NULL OR TRIM(RuntimeMode) = ''");
        });

        await ApplyMigrationAsync(7, "remote_command_logs", async () =>
        {
            await _connection!.CreateTableAsync<RemoteCommandLogEntity>();
        });
    }

    private async Task ApplyMigrationAsync(int version, string name, Func<Task> applyAsync)
    {
        var exists = await _connection!.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM schema_migrations WHERE Version = ?",
            version);
        if (exists > 0)
        {
            return;
        }

        await applyAsync();
        await _connection.ExecuteAsync(
            "INSERT INTO schema_migrations (Version, Name, AppliedAtUtc) VALUES (?, ?, ?)",
            version,
            name,
            DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
    }

    private async Task EnsureColumnAsync(string tableName, string columnName, string definition)
    {
        var sql = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = ?";
        var exists = await _connection!.ExecuteScalarAsync<int>(sql, columnName);
        if (exists > 0)
        {
            return;
        }

        await _connection.ExecuteAsync($"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}");
    }
}
