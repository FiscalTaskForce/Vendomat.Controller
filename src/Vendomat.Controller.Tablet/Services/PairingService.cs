using System.Collections.Concurrent;
using System.Security.Cryptography;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Tablet.Services;

public sealed class PairingService(LocalApiSecurityService localApiSecurityService) : IPairingService
{
    private readonly ConcurrentDictionary<Guid, PairingQrPayload> _activePayloads = new();

    public Task<PairingQrPayload> GenerateAsync(MachineSettings settings, CancellationToken cancellationToken = default)
    {
        return GenerateCoreAsync(settings, cancellationToken);
    }

    public async Task<PairingClaimResult> ClaimAsync(MachineSettings settings, PairingClaimRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupExpiredPayloads();

        if (request.MachineId == Guid.Empty)
        {
            throw new InvalidOperationException("MachineId-ul pentru pairing este lipsa.");
        }

        if (!_activePayloads.TryGetValue(request.MachineId, out var payload))
        {
            throw new InvalidOperationException("Nu exista o sesiune activa de pairing pentru acest dozator.");
        }

        if (payload.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            _activePayloads.TryRemove(request.MachineId, out _);
            throw new InvalidOperationException("Codul de pairing a expirat.");
        }

        if (!string.Equals(payload.PairingCode, request.PairingCode?.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codul de pairing este invalid.");
        }

        return new PairingClaimResult
        {
            MachineId = settings.MachineId,
            MachineName = settings.MachineName,
            LocalApiBaseUrl = NormalizeBaseUrl(settings.LocalApiBaseUrl),
            LocalSecureApiBaseUrl = payload.LocalSecureApiBaseUrl,
            LocalCertificateFingerprint = payload.LocalCertificateFingerprint,
            PublicApiBaseUrl = NormalizeBaseUrl(settings.PublicApiBaseUrl),
            CloudApiBaseUrl = NormalizeBaseUrl(settings.CloudApiBaseUrl),
            CompanionAccessToken = settings.CompanionAccessToken,
            IssuedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private async Task<PairingQrPayload> GenerateCoreAsync(MachineSettings settings, CancellationToken cancellationToken)
    {
        var payload = new PairingQrPayload
        {
            PayloadVersion = 3,
            MachineId = settings.MachineId,
            MachineName = settings.MachineName,
            PairingCode = RandomNumberGenerator.GetInt32(10_000_000, 99_999_999).ToString(),
            LocalApiBaseUrl = NormalizeBaseUrl(settings.LocalApiBaseUrl),
            LocalSecureApiBaseUrl = localApiSecurityService.BuildHttpsBaseUrl(settings.LocalApiBaseUrl),
            LocalCertificateFingerprint = await localApiSecurityService.GetCertificateFingerprintAsync(cancellationToken),
            PublicApiBaseUrl = NormalizeBaseUrl(settings.PublicApiBaseUrl),
            CloudApiBaseUrl = NormalizeBaseUrl(settings.CloudApiBaseUrl),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        };

        CleanupExpiredPayloads();
        _activePayloads.AddOrUpdate(payload.MachineId, payload, (_, _) => payload);
        return payload;
    }

    private void CleanupExpiredPayloads()
    {
        foreach (var entry in _activePayloads)
        {
            if (entry.Value.ExpiresAtUtc < DateTimeOffset.UtcNow)
            {
                _activePayloads.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string NormalizeBaseUrl(string? value) => value?.Trim() ?? string.Empty;
}
