using System.Text.Json;
using Vendomat.Controller.Mobile.Models;

namespace Vendomat.Controller.Mobile.Services;

public sealed class PairedMachineStore(DeviceSecretStore deviceSecretStore)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private string FilePath => Path.Combine(FileSystem.AppDataDirectory, "paired-machines.json");

    public async Task<IReadOnlyList<PairedMachineRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PairedMachineRecord?> GetAsync(Guid machineId, CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken);
        return all.FirstOrDefault(item => item.MachineId == machineId);
    }

    public async Task<bool> ExistsAsync(Guid machineId, CancellationToken cancellationToken = default)
    {
        var item = await GetAsync(machineId, cancellationToken);
        return item is not null;
    }

    public async Task AddOrUpdateAsync(PairedMachineRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadCoreAsync(cancellationToken);
            var existing = all.FirstOrDefault(item => item.MachineId == record.MachineId);
            if (existing is null)
            {
                all.Add(record);
            }
            else
            {
                existing.MachineName = record.MachineName;
                existing.ApiBaseUrl = record.ApiBaseUrl;
                existing.LocalApiBaseUrl = record.LocalApiBaseUrl;
                existing.LocalSecureApiBaseUrl = record.LocalSecureApiBaseUrl;
                existing.LocalCertificateFingerprint = record.LocalCertificateFingerprint;
                existing.PublicApiBaseUrl = record.PublicApiBaseUrl;
                existing.CloudApiBaseUrl = record.CloudApiBaseUrl;
                existing.CompanionAccessToken = record.CompanionAccessToken;
                existing.PairingCode = record.PairingCode;
                existing.PreferredConnectionPreference = record.PreferredConnectionPreference;
                existing.LastConnectionMode = record.LastConnectionMode;
                existing.LastConnectionEndpoint = record.LastConnectionEndpoint;
                existing.LastConnectionCheckedUtc = record.LastConnectionCheckedUtc;
                existing.AddedAtUtc = record.AddedAtUtc;
                existing.LastSeenUtc = record.LastSeenUtc;
                existing.LastSeenOnline = record.LastSeenOnline;
                existing.LastKnownStockLiters = record.LastKnownStockLiters;
                existing.LastKnownTemperatureCelsius = record.LastKnownTemperatureCelsius;
                existing.LastKnownPricePerLiter = record.LastKnownPricePerLiter;
            }

            await SaveCoreAsync(all, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid machineId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadCoreAsync(cancellationToken);
            all.RemoveAll(item => item.MachineId == machineId);
            await SaveCoreAsync(all, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<PairedMachineRecord>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(FilePath);
        var records = await JsonSerializer.DeserializeAsync<List<PairedMachineRecord>>(stream, _serializerOptions, cancellationToken) ?? [];
        foreach (var record in records)
        {
            record.CompanionAccessToken = await deviceSecretStore.UnprotectAsync(record.CompanionAccessToken);
        }

        return records;
    }

    private async Task SaveCoreAsync(List<PairedMachineRecord> records, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var persistedRecords = new List<PairedMachineRecord>(records.Count);
        foreach (var record in records)
        {
            persistedRecords.Add(new PairedMachineRecord
            {
                MachineId = record.MachineId,
                MachineName = record.MachineName,
                ApiBaseUrl = record.ApiBaseUrl,
                LocalApiBaseUrl = record.LocalApiBaseUrl,
                LocalSecureApiBaseUrl = record.LocalSecureApiBaseUrl,
                LocalCertificateFingerprint = record.LocalCertificateFingerprint,
                PublicApiBaseUrl = record.PublicApiBaseUrl,
                CloudApiBaseUrl = record.CloudApiBaseUrl,
                CompanionAccessToken = await deviceSecretStore.ProtectAsync(record.CompanionAccessToken),
                PairingCode = record.PairingCode,
                PreferredConnectionPreference = record.PreferredConnectionPreference,
                LastConnectionMode = record.LastConnectionMode,
                LastConnectionEndpoint = record.LastConnectionEndpoint,
                LastConnectionCheckedUtc = record.LastConnectionCheckedUtc,
                AddedAtUtc = record.AddedAtUtc,
                LastSeenUtc = record.LastSeenUtc,
                LastSeenOnline = record.LastSeenOnline,
                LastKnownStockLiters = record.LastKnownStockLiters,
                LastKnownTemperatureCelsius = record.LastKnownTemperatureCelsius,
                LastKnownPricePerLiter = record.LastKnownPricePerLiter,
            });
        }

        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, persistedRecords, _serializerOptions, cancellationToken);
    }
}
