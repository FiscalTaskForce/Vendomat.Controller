using System.Net.Http.Json;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Tablet.Services;

public sealed class CloudBrokerClient(HttpClient httpClient)
{
    private sealed class SnapshotPublishResponse
    {
        public bool HasActiveWatcher { get; set; }
    }

    private sealed class RevokeResponse
    {
        public int RevokedCount { get; set; }
    }

    public async Task PublishPairingAsync(CloudPairingUpsertRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            BuildUri(request.CloudApiBaseUrl, "api/cloud/machine/pairing"),
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task<CloudMachineSyncResult> SyncMachineAsync(CloudMachineSyncRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            BuildUri(request.CloudApiBaseUrl, "api/cloud/machine/sync"),
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CloudMachineSyncResult>(cancellationToken: cancellationToken)
            ?? new CloudMachineSyncResult();
    }

    public async Task<bool> PublishSnapshotAsync(CloudMachineSyncRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            BuildUri(request.CloudApiBaseUrl, "api/cloud/machine/snapshot"),
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SnapshotPublishResponse>(cancellationToken: cancellationToken);
        return payload?.HasActiveWatcher ?? false;
    }

    public async Task CompleteCommandAsync(CloudCommandCompletionRequest request, string cloudApiBaseUrl, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            BuildUri(cloudApiBaseUrl, "api/cloud/machine/commands/complete"),
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<CloudCompanionSessionInfo>> GetCompanionSessionsAsync(
        string cloudApiBaseUrl,
        CloudMachineCompanionSessionsRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            BuildUri(cloudApiBaseUrl, "api/cloud/machine/companions"),
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<CloudCompanionSessionInfo>>(cancellationToken: cancellationToken)
            ?? [];
    }

    public async Task<int> RevokeCompanionSessionAsync(
        string cloudApiBaseUrl,
        CloudCompanionSessionRevokeRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            BuildUri(cloudApiBaseUrl, "api/cloud/machine/companions/revoke"),
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<RevokeResponse>(cancellationToken: cancellationToken);
        return payload?.RevokedCount ?? 0;
    }

    public async Task<int> RevokeAllCompanionSessionsAsync(
        string cloudApiBaseUrl,
        CloudMachineCompanionSessionsRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            BuildUri(cloudApiBaseUrl, "api/cloud/machine/companions/revoke-all"),
            request,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<RevokeResponse>(cancellationToken: cancellationToken);
        return payload?.RevokedCount ?? 0;
    }

    private static string BuildUri(string apiBaseUrl, string relativePath)
    {
        var baseUrl = apiBaseUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Cloud API base URL is missing.");
        }

        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = $"https://{baseUrl}";
        }

        return $"{baseUrl.TrimEnd('/')}/{relativePath}";
    }
}
