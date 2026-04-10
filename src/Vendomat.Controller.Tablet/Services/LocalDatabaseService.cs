using Android.Util;
using SQLite;
using Microsoft.Maui.Storage;
using Vendomat.Controller.Domain.Security;
using Vendomat.Controller.Tablet.Persistence.Entities;

namespace Vendomat.Controller.Tablet.Services;

public sealed class LocalDatabaseService
{
    private const string StartupTag = "VendomatStartup";
    private SQLiteAsyncConnection? _connection;

    public SQLiteAsyncConnection Connection =>
        _connection ?? throw new InvalidOperationException("Database not initialized.");

    public async Task InitializeAsync()
    {
        if (_connection is not null)
        {
            return;
        }

        Log.Info(StartupTag, "LocalDatabaseService.InitializeAsync start");
        var databasePath = Path.Combine(FileSystem.AppDataDirectory, "vendomat-controller.db3");
        Log.Info(StartupTag, $"Database path: {databasePath}");
        _connection = new SQLiteAsyncConnection(
            databasePath,
            SQLiteOpenFlags.Create | SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex);

        Log.Info(StartupTag, "Creating MachineSettings table");
        await _connection.CreateTableAsync<MachineSettingsEntity>();
        await EnsureMachineSettingsSchemaAsync();
        Log.Info(StartupTag, "Creating Sales table");
        await _connection.CreateTableAsync<SaleTransactionEntity>();
        Log.Info(StartupTag, "Creating DeviceLog table");
        await _connection.CreateTableAsync<DeviceLogEntryEntity>();
        Log.Info(StartupTag, "Creating Sanitation table");
        await _connection.CreateTableAsync<SanitationRecordEntity>();
        Log.Info(StartupTag, "Creating Advertisement table");
        await _connection.CreateTableAsync<AdvertisementAssetEntity>();
        Log.Info(StartupTag, "LocalDatabaseService.InitializeAsync complete");
    }

    private async Task EnsureMachineSettingsSchemaAsync()
    {
        await EnsureColumnAsync(
            tableName: "machine_settings",
            columnName: nameof(MachineSettingsEntity.AdminPasscodeHash),
            definition: "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(
            tableName: "machine_settings",
            columnName: nameof(MachineSettingsEntity.PublicApiBaseUrl),
            definition: "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(
            tableName: "machine_settings",
            columnName: nameof(MachineSettingsEntity.CloudApiBaseUrl),
            definition: "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(
            tableName: "machine_settings",
            columnName: nameof(MachineSettingsEntity.CloudMachineToken),
            definition: "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(
            tableName: "machine_settings",
            columnName: nameof(MachineSettingsEntity.CompanionAccessToken),
            definition: "TEXT NOT NULL DEFAULT ''");

        await _connection!.ExecuteAsync(
            "UPDATE machine_settings SET AdminPasscodeHash = ? WHERE AdminPasscodeHash IS NULL OR TRIM(AdminPasscodeHash) = ''",
            AdminPasscodeHasher.DefaultHash);
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
