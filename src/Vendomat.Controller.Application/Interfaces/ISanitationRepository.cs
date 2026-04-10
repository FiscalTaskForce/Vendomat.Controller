using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Application.Interfaces;

public interface ISanitationRepository
{
    Task SaveAsync(SanitationRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SanitationRecord>> GetRecentAsync(int count = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SanitationRecord>> GetAllAsync(CancellationToken cancellationToken = default);
}
