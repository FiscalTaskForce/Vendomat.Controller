using System.Text.Json;
using Vendomat.Controller.Mobile.Models;

namespace Vendomat.Controller.Mobile.Services;

public sealed class PairedMachineStore
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
                existing.PublicApiBaseUrl = record.PublicApiBaseUrl;
                existing.CloudApiBaseUrl = record.CloudApiBaseUrl;
                existing.CompanionAccessToken = record.CompanionAccessToken;
                existing.PairingCode = record.PairingCode;
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
        return await JsonSerializer.DeserializeAsync<List<PairedMachineRecord>>(stream, _serializerOptions, cancellationToken) ?? [];
    }

    private async Task SaveCoreAsync(List<PairedMachineRecord> records, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, records, _serializerOptions, cancellationToken);
    }
}
