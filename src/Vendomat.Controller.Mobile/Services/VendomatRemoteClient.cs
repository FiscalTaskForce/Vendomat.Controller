using System.Text;
using System.Text.Json;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Mobile.Models;

namespace Vendomat.Controller.Mobile.Services;

public sealed class VendomatRemoteClient(HttpClient httpClient)
{
    private const string CompanionTokenHeaderName = "X-Vendomat-Token";
    private static readonly TimeSpan ReadAttemptTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WriteAttemptTimeout = TimeSpan.FromSeconds(8);

    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public Task<(string ApiBaseUrl, MachineStatusSnapshot Payload)> GetStatusAsync(
        PairedMachineRecord record,
        CancellationToken cancellationToken = default) =>
        SendWithFallbackAsync<MachineStatusSnapshot>(
            record.GetCandidateApiBaseUrls(),
            baseUrl => CreateRequest(HttpMethod.Get, baseUrl, "api/device/status", record.CompanionAccessToken),
            ReadAttemptTimeout,
            cancellationToken);

    public Task<(string ApiBaseUrl, MachineDashboardSnapshot Payload)> GetDashboardAsync(
        PairedMachineRecord record,
        CancellationToken cancellationToken = default) =>
        SendWithFallbackAsync<MachineDashboardSnapshot>(
            record.GetCandidateApiBaseUrls(),
            baseUrl => CreateRequest(HttpMethod.Get, baseUrl, "api/device/dashboard", record.CompanionAccessToken),
            ReadAttemptTimeout,
            cancellationToken);

    public Task<(string ApiBaseUrl, MachineSettings Payload)> GetSettingsAsync(
        PairedMachineRecord record,
        CancellationToken cancellationToken = default) =>
        SendWithFallbackAsync<MachineSettings>(
            record.GetCandidateApiBaseUrls(),
            baseUrl => CreateRequest(HttpMethod.Get, baseUrl, "api/device/settings", record.CompanionAccessToken),
            ReadAttemptTimeout,
            cancellationToken);

    public Task<(string ApiBaseUrl, MachineSettings Payload)> SaveSettingsAsync(
        PairedMachineRecord record,
        MachineSettings settings,
        CancellationToken cancellationToken = default) =>
        SendWithFallbackAsync<MachineSettings>(
            record.GetCandidateApiBaseUrls(),
            baseUrl => CreateRequest(HttpMethod.Put, baseUrl, "api/device/settings", record.CompanionAccessToken, settings),
            WriteAttemptTimeout,
            cancellationToken);

    public Task<string> RunSanitationAsync(
        PairedMachineRecord record,
        SanitationRequest request,
        CancellationToken cancellationToken = default) =>
        SendWithoutBodyWithFallbackAsync(
            record.GetCandidateApiBaseUrls(),
            baseUrl => CreateRequest(HttpMethod.Post, baseUrl, "api/device/sanitation", record.CompanionAccessToken, request),
            WriteAttemptTimeout,
            cancellationToken);

    public async Task<RemoteCreditResult> AddRemoteCreditAsync(
        PairedMachineRecord record,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();

        foreach (var apiBaseUrl in NormalizeCandidates(record.GetCandidateApiBaseUrls()))
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Post, apiBaseUrl, "api/device/credit", record.CompanionAccessToken, new RemoteCreditRequest
                {
                    Amount = amount,
                });
                using var response = await SendAsync(request, WriteAttemptTimeout, cancellationToken);
                var payloadJson = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add($"{apiBaseUrl}: {BuildErrorMessage(response, payloadJson)}");
                    continue;
                }

                if (TryDeserialize(payloadJson, out MachineStatusSnapshot? snapshot) && snapshot is not null)
                {
                    return new RemoteCreditResult
                    {
                        ApiBaseUrl = apiBaseUrl,
                        Snapshot = snapshot,
                    };
                }

                if (TryDeserialize(payloadJson, out RemoteCreditQueuedResponse? queued) && queued?.IsAccepted == true)
                {
                    return new RemoteCreditResult
                    {
                        ApiBaseUrl = apiBaseUrl,
                        IsQueued = true,
                        CommandId = queued.CommandId,
                    };
                }

                failures.Add($"{apiBaseUrl}: Răspunsul API pentru credit remote nu este recunoscut.");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{apiBaseUrl}: {ex.Message}");
            }
        }

        throw new HttpRequestException(
            $"Nu am putut contacta dozatorul pe niciun endpoint configurat. {string.Join(" | ", failures)}");
    }

    public Task<(string ApiBaseUrl, PairingClaimResult Payload)> ClaimPairingAsync(
        PairingQrPayload payload,
        CancellationToken cancellationToken = default)
    {
        var request = new PairingClaimRequest
        {
            MachineId = payload.MachineId,
            PairingCode = payload.PairingCode,
        };

        return SendWithFallbackAsync<PairingClaimResult>(
            GetCandidateApiBaseUrls(payload),
            baseUrl => CreateRequest(HttpMethod.Post, baseUrl, "api/pairing/claim", accessToken: null, request),
            ReadAttemptTimeout,
            cancellationToken);
    }

    private async Task<(string ApiBaseUrl, T Payload)> SendWithFallbackAsync<T>(
        IEnumerable<string> candidateApiBaseUrls,
        Func<string, HttpRequestMessage> requestFactory,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (var apiBaseUrl in NormalizeCandidates(candidateApiBaseUrls))
        {
            try
            {
                using var request = requestFactory(apiBaseUrl);
                using var response = await SendAsync(request, attemptTimeout, cancellationToken);
                response.EnsureSuccessStatusCode();
                var payload = await DeserializeResponseAsync<T>(response, cancellationToken);
                return (apiBaseUrl, payload);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{apiBaseUrl}: {ex.Message}");
            }
        }

        throw new HttpRequestException(
            $"Nu am putut contacta dozatorul pe niciun endpoint configurat. {string.Join(" | ", failures)}");
    }

    private async Task<string> SendWithoutBodyWithFallbackAsync(
        IEnumerable<string> candidateApiBaseUrls,
        Func<string, HttpRequestMessage> requestFactory,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (var apiBaseUrl in NormalizeCandidates(candidateApiBaseUrls))
        {
            try
            {
                using var request = requestFactory(apiBaseUrl);
                using var response = await SendAsync(request, attemptTimeout, cancellationToken);
                response.EnsureSuccessStatusCode();
                return apiBaseUrl;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{apiBaseUrl}: {ex.Message}");
            }
        }

        throw new HttpRequestException(
            $"Nu am putut contacta dozatorul pe niciun endpoint configurat. {string.Join(" | ", failures)}");
    }

    private HttpRequestMessage CreateRequest<TBody>(
        HttpMethod method,
        string apiBaseUrl,
        string relativePath,
        string? accessToken,
        TBody body)
    {
        var request = CreateRequest(method, apiBaseUrl, relativePath, accessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, _serializerOptions),
            Encoding.UTF8,
            "application/json");

        return request;
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string apiBaseUrl,
        string relativePath,
        string? accessToken)
    {
        var request = new HttpRequestMessage(method, BuildUri(apiBaseUrl, relativePath));
        var normalizedToken = accessToken?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedToken))
        {
            request.Headers.TryAddWithoutValidation(CompanionTokenHeaderName, normalizedToken);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(attemptTimeout);
        return await httpClient.SendAsync(request, timeoutCts.Token);
    }

    private static IEnumerable<string> GetCandidateApiBaseUrls(PairingQrPayload payload)
    {
        var candidates = new[]
        {
            payload.CloudApiBaseUrl,
            payload.PublicApiBaseUrl,
            payload.LocalApiBaseUrl,
        };

        return NormalizeCandidates(candidates);
    }

    private static IEnumerable<string> NormalizeCandidates(IEnumerable<string> candidateApiBaseUrls)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidateApiBaseUrls)
        {
            var normalized = candidate?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static string BuildUri(string apiBaseUrl, string relativePath)
    {
        var baseUrl = string.IsNullOrWhiteSpace(apiBaseUrl)
            ? throw new InvalidOperationException("API base URL is missing.")
            : apiBaseUrl.Trim().TrimEnd('/');

        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = $"http://{baseUrl}";
        }

        return $"{baseUrl}/{relativePath}";
    }

    private async Task<T> DeserializeResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _serializerOptions, cancellationToken)
            ?? throw new InvalidOperationException("Response payload was empty.");
    }

    private bool TryDeserialize<T>(string payloadJson, out T? payload)
    {
        payload = default;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<T>(payloadJson, _serializerOptions);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string BuildErrorMessage(HttpResponseMessage response, string payloadJson)
    {
        var fallback = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        if (!TryDeserialize(payloadJson, out ApiErrorResponse? apiError) || apiError is null)
        {
            return fallback;
        }

        var message = !string.IsNullOrWhiteSpace(apiError.Message)
            ? apiError.Message
            : apiError.Error;

        return string.IsNullOrWhiteSpace(message)
            ? fallback
            : $"{fallback}: {message}";
    }

    private sealed class ApiErrorResponse
    {
        public string? Error { get; set; }
        public string? Message { get; set; }
    }

    private sealed class RemoteCreditQueuedResponse
    {
        public string Status { get; set; } = string.Empty;
        public Guid? CommandId { get; set; }
        public bool? Success { get; set; }

        public bool IsAccepted =>
            Success == true
            || string.Equals(Status, "accepted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "queued", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);
    }
}
