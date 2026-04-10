using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Application.Interfaces;

public interface ISalesRepository
{
    Task SaveAsync(SaleTransaction transaction, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SaleTransaction>> GetRecentAsync(int count = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SaleTransaction>> GetAllAsync(CancellationToken cancellationToken = default);
}
