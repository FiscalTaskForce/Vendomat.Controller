using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Tablet.Persistence.Entities;

namespace Vendomat.Controller.Tablet.Services;

public sealed class OperationalBackupService(
    LocalDatabaseService database,
    IMachineSettingsRepository machineSettingsRepository,
    ISalesRepository salesRepository,
    ILogRepository logRepository,
    ISanitationRepository sanitationRepository,
    IAdvertisementRepository advertisementRepository,
    RemoteCommandJournal remoteCommandJournal)
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<string> ExportAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();

        var settings = await machineSettingsRepository.GetAsync(cancellationToken);
        var sales = await salesRepository.GetAllAsync(cancellationToken);
        var logs = await logRepository.GetAllAsync(cancellationToken);
        var sanitations = await sanitationRepository.GetAllAsync(cancellationToken);
        var ads = await advertisementRepository.GetAllAsync(cancellationToken);
        var remoteCommands = await remoteCommandJournal.GetAllAsync(cancellationToken);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backupDirectory = Path.Combine(FileSystem.AppDataDirectory, "backups", stamp);
        Directory.CreateDirectory(backupDirectory);

        var archive = new BackupArchive
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SchemaVersion = 1,
            Settings = settings,
            Sales = sales.ToList(),
            Logs = logs.ToList(),
            Sanitations = sanitations.ToList(),
            Advertisements = ads.ToList(),
            RemoteCommands = remoteCommands.Select(item => new BackupRemoteCommandLog
            {
                CommandId = item.CommandId,
                CommandType = item.CommandType,
                PayloadHash = item.PayloadHash,
                Status = item.Status,
                ResultMessage = item.ResultMessage,
                CreatedAtUtcTicks = item.CreatedAtUtcTicks,
                UpdatedAtUtcTicks = item.UpdatedAtUtcTicks,
            }).ToList(),
        };

        await File.WriteAllTextAsync(
            Path.Combine(backupDirectory, "backup.json"),
            JsonSerializer.Serialize(archive, _jsonOptions),
            cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "sales.csv"), BuildSalesCsv(sales), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "sanitations.csv"), BuildSanitationsCsv(sanitations), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "logs.csv"), BuildLogsCsv(logs), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "remote-commands.csv"), BuildRemoteCommandsCsv(remoteCommands), cancellationToken);

        return backupDirectory;
    }

    public async Task<string?> RestoreLatestAsync(CancellationToken cancellationToken = default)
    {
        var rootDirectory = Path.Combine(FileSystem.AppDataDirectory, "backups");
        if (!Directory.Exists(rootDirectory))
        {
            return null;
        }

        var latestBackupFile = Directory.GetFiles(rootDirectory, "backup.json", SearchOption.AllDirectories)
            .OrderByDescending(path => path)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(latestBackupFile))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(latestBackupFile, cancellationToken);
        var archive = JsonSerializer.Deserialize<BackupArchive>(json, _jsonOptions)
            ?? throw new InvalidOperationException("Backup invalid.");

        await database.InitializeAsync();
        await machineSettingsRepository.SaveAsync(archive.Settings ?? new MachineSettings(), cancellationToken);

        await database.Connection.DeleteAllAsync<SaleTransactionEntity>();
        await database.Connection.InsertAllAsync((archive.Sales ?? []).Select(SaleTransactionEntity.FromDomain).ToList());

        await database.Connection.DeleteAllAsync<DeviceLogEntryEntity>();
        await database.Connection.InsertAllAsync((archive.Logs ?? []).Select(DeviceLogEntryEntity.FromDomain).ToList());

        await database.Connection.DeleteAllAsync<SanitationRecordEntity>();
        await database.Connection.InsertAllAsync((archive.Sanitations ?? []).Select(SanitationRecordEntity.FromDomain).ToList());

        await database.Connection.DeleteAllAsync<AdvertisementAssetEntity>();
        await database.Connection.InsertAllAsync((archive.Advertisements ?? []).Select(AdvertisementAssetEntity.FromDomain).ToList());

        await database.Connection.DeleteAllAsync<RemoteCommandLogEntity>();
        await database.Connection.InsertAllAsync((archive.RemoteCommands ?? []).Select(item => new RemoteCommandLogEntity
        {
            CommandId = item.CommandId,
            CommandType = item.CommandType,
            PayloadHash = item.PayloadHash,
            Status = item.Status,
            ResultMessage = item.ResultMessage,
            CreatedAtUtcTicks = item.CreatedAtUtcTicks,
            UpdatedAtUtcTicks = item.UpdatedAtUtcTicks,
        }).ToList());

        return latestBackupFile;
    }

    private static string BuildSalesCsv(IReadOnlyList<SaleTransaction> sales)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Id,MachineId,RequestedLiters,DispensedLiters,TotalAmount,PaymentMethod,Status,StartedAtUtc,CompletedAtUtc");
        foreach (var sale in sales)
        {
            csv.AppendLine(string.Join(",",
                Csv(sale.Id.ToString("N")),
                Csv(sale.MachineId.ToString("N")),
                Csv(sale.RequestedLiters),
                Csv(sale.DispensedLiters),
                Csv(sale.TotalAmount),
                Csv(sale.PaymentMethod),
                Csv(sale.Status),
                Csv(sale.StartedAtUtc),
                Csv(sale.CompletedAtUtc)));
        }

        return csv.ToString();
    }

    private static string BuildSanitationsCsv(IReadOnlyList<SanitationRecord> sanitations)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Id,MachineId,Mode,Duration,PulseOn,PulseOff,Notes,StartedAtUtc");
        foreach (var item in sanitations)
        {
            csv.AppendLine(string.Join(",",
                Csv(item.Id.ToString("N")),
                Csv(item.MachineId.ToString("N")),
                Csv(item.Mode),
                Csv(item.Duration),
                Csv(item.PulseOn),
                Csv(item.PulseOff),
                Csv(item.Notes),
                Csv(item.StartedAtUtc)));
        }

        return csv.ToString();
    }

    private static string BuildLogsCsv(IReadOnlyList<DeviceLogEntry> logs)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Id,Severity,Category,Message,Details,CreatedAtUtc");
        foreach (var item in logs)
        {
            csv.AppendLine(string.Join(",",
                Csv(item.Id.ToString("N")),
                Csv(item.Severity),
                Csv(item.Category),
                Csv(item.Message),
                Csv(item.Details),
                Csv(item.CreatedAtUtc)));
        }

        return csv.ToString();
    }

    private static string BuildRemoteCommandsCsv(IReadOnlyList<RemoteCommandLogEntity> commands)
    {
        var csv = new StringBuilder();
        csv.AppendLine("CommandId,CommandType,PayloadHash,Status,ResultMessage,CreatedAtUtc,UpdatedAtUtc");
        foreach (var item in commands)
        {
            csv.AppendLine(string.Join(",",
                Csv(item.CommandId),
                Csv(item.CommandType),
                Csv(item.PayloadHash),
                Csv(item.Status),
                Csv(item.ResultMessage),
                Csv(new DateTimeOffset(item.CreatedAtUtcTicks, TimeSpan.Zero)),
                Csv(new DateTimeOffset(item.UpdatedAtUtcTicks, TimeSpan.Zero))));
        }

        return csv.ToString();
    }

    private static string Csv(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private sealed class BackupArchive
    {
        public int SchemaVersion { get; set; }
        public DateTimeOffset GeneratedAtUtc { get; set; }
        public MachineSettings? Settings { get; set; }
        public List<SaleTransaction> Sales { get; set; } = [];
        public List<DeviceLogEntry> Logs { get; set; } = [];
        public List<SanitationRecord> Sanitations { get; set; } = [];
        public List<AdvertisementAsset> Advertisements { get; set; } = [];
        public List<BackupRemoteCommandLog> RemoteCommands { get; set; } = [];
    }

    private sealed class BackupRemoteCommandLog
    {
        public string CommandId { get; set; } = string.Empty;
        public string CommandType { get; set; } = string.Empty;
        public string PayloadHash { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ResultMessage { get; set; } = string.Empty;
        public long CreatedAtUtcTicks { get; set; }
        public long UpdatedAtUtcTicks { get; set; }
    }
}
