using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Application.Interfaces;

public interface IMachineSettingsRepository
{
    Task<MachineSettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(MachineSettings settings, CancellationToken cancellationToken = default);
}
