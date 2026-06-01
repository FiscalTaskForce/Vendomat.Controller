using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Tablet.Persistence.Entities;

namespace Vendomat.Controller.Tablet.Services;

public sealed class SqliteLogRepository(LocalDatabaseService database) : ILogRepository
{
    public async Task SaveAsync(DeviceLogEntry entry, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        await database.Connection.InsertOrReplaceAsync(DeviceLogEntryEntity.FromDomain(entry));
    }

    public async Task<IReadOnlyList<DeviceLogEntry>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var rows = await database.Connection.Table<DeviceLogEntryEntity>()
            .OrderByDescending(x => x.CreatedAtUtcTicks)
            .Take(count)
            .ToListAsync();

        return rows.Select(x => x.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<DeviceLogEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var rows = await database.Connection.Table<DeviceLogEntryEntity>()
            .OrderByDescending(x => x.CreatedAtUtcTicks)
            .ToListAsync();

        return rows.Select(x => x.ToDomain()).ToList();
    }
}
