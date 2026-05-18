using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Mobile.Models;

namespace Vendomat.Controller.Mobile.Services;

public sealed class VendomatRemoteClient
{
    private const string CompanionTokenHeaderName = "X-Vendomat-Token";
    private static readonly TimeSpan ReadAttemptTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WriteAttemptTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly SemaphoreSlim _tunnelLock = new(1, 1);
    private ClientWebSocket? _tunnelSocket;
    private string _tunnelSessionKey = string.Empty;

    public VendomatRemoteClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _serializerOptions.Converters.Add(new FlexibleDateTimeOffsetJsonConverter());
        _serializerOptions.Converters.Add(new NullableFlexibleDateTimeOffsetJsonConverter());
    }

    public Task<(string ApiBaseUrl, MachineStatusSnapshot Payload)> GetStatusAsync(
        PairedMachineRecord record,
        CancellationToken cancellationToken = default) =>
        SendWithFallbackAsync<MachineStatusSnapshot>(
            record,
            CloudTunnelActions.GetStatus,
            tunnelPayload: null,
            baseUrl => CreateRequest(HttpMethod.Get, baseUrl, "api/device/status", record.CompanionAccessToken),
            ReadAttemptTimeout,
            cancellationToken);

    public Task<(string ApiBaseUrl, MachineDashboardSnapshot Payload)> GetDashboardAsync(
        PairedMachineRecord record,
        CancellationToken cancellationToken = default) =>
        SendWithFallbackAsync<MachineDashboardSnapshot>(
            record,
            CloudTunnelActions.GetDashboard,
            tunnelPayload: null,
            baseUrl => CreateRequest(HttpMethod.Get, baseUrl, "api/device/dashboard", record.CompanionAccessToken),
            ReadAttemptTimeout,
            cancellationToken);

    public Task<(string ApiBaseUrl, MachineSettings Payload)> GetSettingsAsync(
        PairedMachineRecord record,
        CancellationToken cancellationToken = default) =>
        SendWithFallbackAsync<MachineSettings>(
            record,
            CloudTunnelActions.GetSettings,
            tunnelPayload: null,
            baseUrl => CreateRequest(HttpMethod.Get, baseUrl, "api/device/settings", record.CompanionAccessToken),
            ReadAttemptTimeout,
            cancellationToken);

    public Task<(string ApiBaseUrl, MachineSettings Payload)> SaveSettingsAsync(
        PairedMachineRecord record,
        MachineSettings settings,
        CancellationToken cancellationToken = default) =>
        SendWithFallbackAsync<MachineSettings>(
            record,
            CloudTunnelActions.SaveSettings,
            settings,
            baseUrl => CreateRequest(HttpMethod.Put, baseUrl, "api/device/settings", record.CompanionAccessToken, settings),
            WriteAttemptTimeout,
            cancellationToken);

    public Task<string> RunSanitationAsync(
        PairedMachineRecord record,
        SanitationRequest request,
        CancellationToken cancellationToken = default) =>
        SendWithoutBodyWithFallbackAsync(
            record,
            CloudTunnelActions.RunSanitation,
            request,
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
                if (ShouldUseCloudTunnel(record, apiBaseUrl))
                {
                    var payload = await SendTunnelAsync<CloudTunnelRemoteCreditResponse>(
                        record,
                        CloudTunnelActions.AddCredit,
                        new RemoteCreditRequest
                        {
                            Amount = amount,
                        },
                        WriteAttemptTimeout,
                        cancellationToken);

                    if (payload.Snapshot is not null)
                    {
                        return new RemoteCreditResult
                        {
                            ApiBaseUrl = apiBaseUrl,
                            Snapshot = payload.Snapshot,
                        };
                    }

                    if (payload.IsQueued)
                    {
                        return new RemoteCreditResult
                        {
                            ApiBaseUrl = apiBaseUrl,
                            IsQueued = true,
                            CommandId = payload.CommandId,
                        };
                    }

                    failures.Add($"{apiBaseUrl}: Raspunsul tunelului pentru credit remote nu este recunoscut.");
                    continue;
                }

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

                failures.Add($"{apiBaseUrl}: Raspunsul API pentru credit remote nu este recunoscut.");
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

        return SendClaimWithFallbackAsync(
            GetCandidateApiBaseUrls(payload),
            baseUrl => CreateRequest(HttpMethod.Post, baseUrl, "api/pairing/claim", accessToken: null, request),
            ReadAttemptTimeout,
            cancellationToken);
    }

    private async Task<(string ApiBaseUrl, T Payload)> SendWithFallbackAsync<T>(
        PairedMachineRecord record,
        string tunnelAction,
        object? tunnelPayload,
        Func<string, HttpRequestMessage> requestFactory,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (var apiBaseUrl in NormalizeCandidates(record.GetCandidateApiBaseUrls()))
        {
            try
            {
                var payload = ShouldUseCloudTunnel(record, apiBaseUrl)
                    ? await SendTunnelAsync<T>(record, tunnelAction, tunnelPayload, attemptTimeout, cancellationToken)
                    : await SendHttpAsync<T>(requestFactory(apiBaseUrl), attemptTimeout, cancellationToken);

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
        PairedMachineRecord record,
        string tunnelAction,
        object? tunnelPayload,
        Func<string, HttpRequestMessage> requestFactory,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (var apiBaseUrl in NormalizeCandidates(record.GetCandidateApiBaseUrls()))
        {
            try
            {
                if (ShouldUseCloudTunnel(record, apiBaseUrl))
                {
                    _ = await SendTunnelAsync<CloudTunnelAcceptedResponse>(
                        record,
                        tunnelAction,
                        tunnelPayload,
                        attemptTimeout,
                        cancellationToken);
                }
                else
                {
                    using var request = requestFactory(apiBaseUrl);
                    using var response = await SendAsync(request, attemptTimeout, cancellationToken);
                    response.EnsureSuccessStatusCode();
                }

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

    private async Task<(string ApiBaseUrl, PairingClaimResult Payload)> SendClaimWithFallbackAsync(
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
                var payload = await DeserializeResponseAsync<PairingClaimResult>(response, cancellationToken);
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

    private async Task<T> SendHttpAsync<T>(
        HttpRequestMessage request,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, attemptTimeout, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await DeserializeResponseAsync<T>(response, cancellationToken);
    }

    private async Task<T> SendTunnelAsync<T>(
        PairedMachineRecord record,
        string action,
        object? payload,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        await _tunnelLock.WaitAsync(cancellationToken);
        try
        {
            var socket = await EnsureTunnelSocketAsync(record, cancellationToken);
            var requestId = Guid.NewGuid().ToString("N");
            var envelope = new CloudTunnelEnvelope
            {
                MessageType = CloudTunnelMessageTypes.Request,
                RequestId = requestId,
                MachineId = record.MachineId,
                Action = action,
                Payload = SerializePayload(payload),
            };

            await SendEnvelopeAsync(socket, envelope, attemptTimeout, cancellationToken);
            var response = await ReceiveEnvelopeAsync(socket, attemptTimeout, cancellationToken);
            if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Tunnel response correlation mismatch.");
            }

            if (!response.Success)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(response.ErrorMessage)
                        ? "Tunnel request failed."
                        : response.ErrorMessage);
            }

            return DeserializePayload<T>(response.Payload);
        }
        catch
        {
            await ResetTunnelSocketAsync();
            throw;
        }
        finally
        {
            _tunnelLock.Release();
        }
    }

    private async Task<ClientWebSocket> EnsureTunnelSocketAsync(
        PairedMachineRecord record,
        CancellationToken cancellationToken)
    {
        var sessionKey = BuildTunnelSessionKey(record);
        if (_tunnelSocket is not null
            && _tunnelSocket.State == WebSocketState.Open
            && string.Equals(_tunnelSessionKey, sessionKey, StringComparison.Ordinal))
        {
            return _tunnelSocket;
        }

        await ResetTunnelSocketAsync();

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader(CompanionTokenHeaderName, record.CompanionAccessToken.Trim());
        await socket.ConnectAsync(BuildWebSocketUri(record.CloudApiBaseUrl, "ws/companion"), cancellationToken);

        _tunnelSocket = socket;
        _tunnelSessionKey = sessionKey;
        return socket;
    }

    private async Task ResetTunnelSocketAsync()
    {
        var socket = _tunnelSocket;
        _tunnelSocket = null;
        _tunnelSessionKey = string.Empty;

        if (socket is null)
        {
            return;
        }

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Tunnel reset.", CancellationToken.None);
            }
        }
        catch
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    private async Task SendEnvelopeAsync(
        ClientWebSocket socket,
        CloudTunnelEnvelope envelope,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(envelope, _serializerOptions);
        var buffer = Encoding.UTF8.GetBytes(json);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await socket.SendAsync(buffer, WebSocketMessageType.Text, endOfMessage: true, timeoutCts.Token);
    }

    private async Task<CloudTunnelEnvelope> ReceiveEnvelopeAsync(
        ClientWebSocket socket,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var json = await ReceiveTextAsync(socket, timeoutCts.Token);
        return JsonSerializer.Deserialize<CloudTunnelEnvelope>(json, _serializerOptions)
            ?? throw new InvalidOperationException("Tunnel response is invalid.");
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Tunnel connection closed.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
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
        return await _httpClient.SendAsync(request, timeoutCts.Token);
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

    private static bool ShouldUseCloudTunnel(PairedMachineRecord record, string apiBaseUrl) =>
        !string.IsNullOrWhiteSpace(record.CloudApiBaseUrl)
        && string.Equals(
            NormalizeBaseUrl(record.CloudApiBaseUrl),
            NormalizeBaseUrl(apiBaseUrl),
            StringComparison.OrdinalIgnoreCase);

    private static string BuildTunnelSessionKey(PairedMachineRecord record) =>
        $"{record.MachineId:N}|{NormalizeBaseUrl(record.CloudApiBaseUrl)}|{record.CompanionAccessToken?.Trim()}";

    private static string NormalizeBaseUrl(string? apiBaseUrl) =>
        apiBaseUrl?.Trim().TrimEnd('/') ?? string.Empty;

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

    private static Uri BuildWebSocketUri(string apiBaseUrl, string relativePath)
    {
        var normalized = apiBaseUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Cloud API base URL is missing.");
        }

        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"https://{normalized}";
        }

        var baseUri = new Uri(normalized.TrimEnd('/') + "/", UriKind.Absolute);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
        };

        return new Uri(builder.Uri, relativePath);
    }

    private async Task<T> DeserializeResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _serializerOptions, cancellationToken)
            ?? throw new InvalidOperationException("Response payload was empty.");
    }

    private T DeserializePayload<T>(JsonElement payload) =>
        payload.Deserialize<T>(_serializerOptions)
        ?? throw new InvalidOperationException("Tunnel payload was empty.");

    private JsonElement SerializePayload(object? payload) =>
        payload is null
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(payload, _serializerOptions);

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
