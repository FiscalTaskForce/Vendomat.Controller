using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Tablet.Persistence.Entities;

namespace Vendomat.Controller.Tablet.Services;

public sealed class SqliteSalesRepository(LocalDatabaseService database) : ISalesRepository
{
    public async Task SaveAsync(SaleTransaction transaction, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        await database.Connection.InsertOrReplaceAsync(SaleTransactionEntity.FromDomain(transaction));
    }

    public async Task<IReadOnlyList<SaleTransaction>> GetRecentAsync(int count = 50, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var rows = await database.Connection.Table<SaleTransactionEntity>()
            .OrderByDescending(x => x.StartedAtUtcTicks)
            .Take(count)
            .ToListAsync();

        return rows.Select(x => x.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<SaleTransaction>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var rows = await database.Connection.Table<SaleTransactionEntity>()
            .OrderByDescending(x => x.StartedAtUtcTicks)
            .ToListAsync();

        return rows.Select(x => x.ToDomain()).ToList();
    }
}
