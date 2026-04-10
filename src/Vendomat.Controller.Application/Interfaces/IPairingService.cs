using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Application.Interfaces;

public interface IPairingService
{
    Task<PairingQrPayload> GenerateAsync(MachineSettings settings, CancellationToken cancellationToken = default);
    Task<PairingClaimResult> ClaimAsync(MachineSettings settings, PairingClaimRequest request, CancellationToken cancellationToken = default);
}
