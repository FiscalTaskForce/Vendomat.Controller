using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Tablet.Persistence.Entities;

namespace Vendomat.Controller.Tablet.Services;

public sealed class SqliteSanitationRepository(LocalDatabaseService database) : ISanitationRepository
{
    public async Task SaveAsync(SanitationRecord record, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        await database.Connection.InsertOrReplaceAsync(SanitationRecordEntity.FromDomain(record));
    }

    public async Task<IReadOnlyList<SanitationRecord>> GetRecentAsync(int count = 50, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var rows = await database.Connection.Table<SanitationRecordEntity>()
            .OrderByDescending(x => x.StartedAtUtcTicks)
            .Take(count)
            .ToListAsync();

        return rows.Select(x => x.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<SanitationRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var rows = await database.Connection.Table<SanitationRecordEntity>()
            .OrderByDescending(x => x.StartedAtUtcTicks)
            .ToListAsync();

        return rows.Select(x => x.ToDomain()).ToList();
    }
}
