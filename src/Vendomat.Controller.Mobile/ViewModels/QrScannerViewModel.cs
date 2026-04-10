using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using Vendomat.Controller.Client.Localization;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Mobile.Models;
using Vendomat.Controller.Mobile.Services;

namespace Vendomat.Controller.Mobile.ViewModels;

public partial class QrScannerViewModel(
    PairedMachineStore pairedMachineStore,
    VendomatRemoteClient remoteClient,
    LanguageService languageService) : ObservableObject
{
    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool isProcessing;

    [ObservableProperty]
    private bool isManualPairingVisible;

    [ObservableProperty]
    private string manualRawPayload = string.Empty;

    [ObservableProperty]
    private string manualMachineId = string.Empty;

    [ObservableProperty]
    private string manualPairingCode = string.Empty;

    [ObservableProperty]
    private string manualCloudApiBaseUrl = string.Empty;

    [ObservableProperty]
    private string manualPublicApiBaseUrl = string.Empty;

    [ObservableProperty]
    private string manualLocalApiBaseUrl = string.Empty;

    public QrScannerViewModel() : this(
        ServiceRegistry.GetRequiredService<PairedMachineStore>(),
        ServiceRegistry.GetRequiredService<VendomatRemoteClient>(),
        ServiceRegistry.GetRequiredService<LanguageService>())
    {
    }

    public void Reset()
    {
        IsProcessing = false;
        IsManualPairingVisible = false;
        StatusMessage = T(nameof(AppLanguageStrings.MobileScannerManualHint));
    }

    public string GetPermissionHintMessage() =>
        T(nameof(AppLanguageStrings.MobileScannerPermissionRequired));

    public async Task<bool> HandleScanAsync(string rawValue)
    {
        if (!TryParsePayload(rawValue, out var payload))
        {
            StatusMessage = T(nameof(AppLanguageStrings.MobileScannerInvalidQr));
            return false;
        }

        return await HandlePayloadAsync(payload!);
    }

    public async Task<bool> ApplyManualLaunchRequestAsync(ManualPairingLaunchRequest request)
    {
        Debug.WriteLine($"[ManualPairing] Apply request AutoSubmit={request.AutoSubmit} MachineId={request.MachineId} Cloud={request.CloudApiBaseUrl}");
        ManualRawPayload = request.RawPayload?.Trim() ?? string.Empty;
        ManualMachineId = request.MachineId?.Trim() ?? string.Empty;
        ManualPairingCode = request.PairingCode?.Trim() ?? string.Empty;
        ManualCloudApiBaseUrl = request.CloudApiBaseUrl?.Trim() ?? string.Empty;
        ManualPublicApiBaseUrl = request.PublicApiBaseUrl?.Trim() ?? string.Empty;
        ManualLocalApiBaseUrl = request.LocalApiBaseUrl?.Trim() ?? string.Empty;
        IsManualPairingVisible = false;

        if (!request.HasAnyValue)
        {
            StatusMessage = T(nameof(AppLanguageStrings.MobileScannerManualHint));
            return false;
        }

        return await SubmitManualPairingAsync();
    }

    [RelayCommand]
    private void ShowManualPairing()
    {
        IsManualPairingVisible = true;
        StatusMessage = T(nameof(AppLanguageStrings.MobileScannerManualOpenStatus));
    }

    [RelayCommand]
    private void HideManualPairing() => IsManualPairingVisible = false;

    [RelayCommand]
    private Task<bool> SubmitManualPairing() => SubmitManualPairingAsync();

    private async Task<bool> SubmitManualPairingAsync()
    {
        Debug.WriteLine($"[ManualPairing] Submit started RawPayload={(!string.IsNullOrWhiteSpace(ManualRawPayload))} MachineId={ManualMachineId}");
        PairingQrPayload? payload = null;

        if (!string.IsNullOrWhiteSpace(ManualRawPayload))
        {
            if (!TryParsePayload(ManualRawPayload, out payload))
            {
                StatusMessage = T(nameof(AppLanguageStrings.MobileScannerInvalidQr));
                return false;
            }
        }
        else
        {
            payload = BuildManualPayload();
            if (payload is null)
            {
                StatusMessage = T(nameof(AppLanguageStrings.MobileScannerManualInvalidData));
                return false;
            }
        }

        return await HandlePayloadAsync(payload!);
    }

    private async Task<bool> HandlePayloadAsync(PairingQrPayload payload)
    {
        if (IsProcessing)
        {
            return false;
        }

        IsProcessing = true;

        try
        {
            Debug.WriteLine($"[ManualPairing] Handle payload MachineId={payload.MachineId} Cloud={payload.CloudApiBaseUrl} Public={payload.PublicApiBaseUrl} Local={payload.LocalApiBaseUrl}");
            if (payload.MachineId == Guid.Empty || !HasAnyEndpoint(payload))
            {
                StatusMessage = T(nameof(AppLanguageStrings.MobileScannerInvalidQr));
                return false;
            }

            if (payload.ExpiresAtUtc < DateTimeOffset.UtcNow)
            {
                StatusMessage = T(nameof(AppLanguageStrings.MobileScannerExpiredQr));
                return false;
            }

            if (await pairedMachineStore.ExistsAsync(payload.MachineId))
            {
                StatusMessage = T(nameof(AppLanguageStrings.MobileScannerAlreadyAdded));
                return false;
            }

            var (claimApiBaseUrl, claimResult) = await remoteClient.ClaimPairingAsync(payload);
            Debug.WriteLine($"[ManualPairing] Claim ok via {claimApiBaseUrl} MachineName={claimResult.MachineName}");
            var record = new PairedMachineRecord
            {
                MachineId = claimResult.MachineId,
                MachineName = claimResult.MachineName,
                ApiBaseUrl = claimApiBaseUrl,
                LocalApiBaseUrl = claimResult.LocalApiBaseUrl,
                PublicApiBaseUrl = claimResult.PublicApiBaseUrl,
                CloudApiBaseUrl = claimResult.CloudApiBaseUrl,
                CompanionAccessToken = claimResult.CompanionAccessToken,
                PairingCode = payload.PairingCode,
                AddedAtUtc = DateTimeOffset.UtcNow,
            };

            var (statusApiBaseUrl, snapshot) = await remoteClient.GetStatusAsync(record);
            Debug.WriteLine($"[ManualPairing] Status ok via {statusApiBaseUrl} Stock={snapshot.Settings.CurrentStockLiters}");
            record.ApiBaseUrl = statusApiBaseUrl;
            record.LastSeenOnline = true;
            record.LastSeenUtc = snapshot.GeneratedAtUtc;
            record.LastKnownStockLiters = snapshot.Settings.CurrentStockLiters;
            record.LastKnownTemperatureCelsius = snapshot.Sensor.TemperatureCelsius;
            record.LastKnownPricePerLiter = snapshot.Settings.PricePerLiter;
            await pairedMachineStore.AddOrUpdateAsync(record);
            StatusMessage = T(nameof(AppLanguageStrings.MobileScannerPairSuccess));
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ManualPairing] Failed: {ex}");
            StatusMessage = T(nameof(AppLanguageStrings.MobileScannerConnectionError));
            return false;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private PairingQrPayload? BuildManualPayload()
    {
        if (!Guid.TryParse(ManualMachineId, out var machineId) || string.IsNullOrWhiteSpace(ManualPairingCode))
        {
            return null;
        }

        var payload = new PairingQrPayload
        {
            MachineId = machineId,
            PairingCode = ManualPairingCode.Trim(),
            CloudApiBaseUrl = ManualCloudApiBaseUrl.Trim(),
            PublicApiBaseUrl = ManualPublicApiBaseUrl.Trim(),
            LocalApiBaseUrl = ManualLocalApiBaseUrl.Trim(),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        };

        return HasAnyEndpoint(payload) ? payload : null;
    }

    private static bool TryParsePayload(string rawValue, out PairingQrPayload? payload)
    {
        var isValidPayload = PairingQrPayload.TryParse(rawValue, out payload);
        return isValidPayload && payload is not null && HasAnyEndpoint(payload);
    }

    private static bool HasAnyEndpoint(PairingQrPayload payload) =>
        !string.IsNullOrWhiteSpace(payload.LocalApiBaseUrl)
        || !string.IsNullOrWhiteSpace(payload.PublicApiBaseUrl)
        || !string.IsNullOrWhiteSpace(payload.CloudApiBaseUrl);

    private string T(string key) => languageService.GetText(key);
}
