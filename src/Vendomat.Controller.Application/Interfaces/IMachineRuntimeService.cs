using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Application.Interfaces;

public interface IMachineRuntimeService
{
    Task<MachineStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<MachineDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<MachineSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(MachineSettings settings, CancellationToken cancellationToken = default);
    Task SetPaymentMethodAsync(PaymentMethod paymentMethod, CancellationToken cancellationToken = default);
    Task SetRequestedLitersAsync(decimal liters, CancellationToken cancellationToken = default);
    Task AddCreditAsync(decimal amount, CancellationToken cancellationToken = default);
    Task AddRemoteCreditAsync(decimal amount, CancellationToken cancellationToken = default);
    Task StartDispenseAsync(DispenseCommand command, CancellationToken cancellationToken = default);
    Task RunSanitationAsync(SanitationRequest request, CancellationToken cancellationToken = default);
    Task<PairingQrPayload> GeneratePairingAsync(CancellationToken cancellationToken = default);
    Task<PairingClaimResult> ClaimPairingAsync(PairingClaimRequest request, CancellationToken cancellationToken = default);
    Task<bool> ValidateCompanionAccessTokenAsync(string? accessToken, CancellationToken cancellationToken = default);
}
