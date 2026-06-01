using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Application.Interfaces;

public interface ILogRepository
{
    Task SaveAsync(DeviceLogEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceLogEntry>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceLogEntry>> GetAllAsync(CancellationToken cancellationToken = default);
}
