using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Android.Util;
using EmbedIO;
using EmbedIO.Actions;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Security;

namespace Vendomat.Controller.Tablet.Services;

public sealed class LocalApiHostService(
    IMachineRuntimeService machineRuntimeService,
    RemoteCommandJournal remoteCommandJournal,
    CloudBridgeService cloudBridgeService,
    LocalApiSecurityService localApiSecurityService) : ILocalApiHost
{
    private const string StartupTag = "VendomatStartup";
    private const string CompanionTokenHeaderName = "X-Vendomat-Token";
    private const int HttpPort = 1326;
    private const int HttpsPort = 1327;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private WebServer? _httpServer;
    private WebServer? _httpsServer;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_httpServer is not null || _httpsServer is not null)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_httpServer is not null || _httpsServer is not null)
            {
                return;
            }

            Log.Info(StartupTag, "Configuring local API host");
            await localApiSecurityService.OpenFirewallPortsAsync(cancellationToken);

            _httpServer = BuildServer($"http://*:{HttpPort}/", cancellationToken);
            await Task.Run(() =>
            {
                Log.Info(StartupTag, "Starting local API host listener");
                _httpServer.Start();
            }, cancellationToken);

            try
            {
                var certificate = await localApiSecurityService.GetServerCertificateAsync(cancellationToken);
                _httpsServer = BuildServer($"https://*:{HttpsPort}/", cancellationToken, certificate);
                await Task.Run(() => _httpsServer.Start(), cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _httpsServer?.Dispose();
                _httpsServer = null;
                Log.Warn(StartupTag, $"HTTPS listener unavailable: {ex.Message}");
            }

            Log.Info(StartupTag, "Local API host listener started");
        }
        finally
        {
            _startLock.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_httpServer is not null || _httpsServer is not null)
        {
            Log.Info(StartupTag, "Stopping local API host");
        }

        _httpServer?.Dispose();
        _httpServer = null;
        _httpsServer?.Dispose();
        _httpsServer = null;
        return Task.CompletedTask;
    }

    private WebServer BuildServer(string urlPrefix, CancellationToken cancellationToken, X509Certificate2? certificate = null)
    {
        return new WebServer(options =>
            {
                options.WithUrlPrefix(urlPrefix);
                options.WithMode(HttpListenerMode.EmbedIO);
                if (certificate is not null)
                {
                    options.WithCertificate(certificate);
                }
            })
            .WithModule(new ActionModule("/api/health", HttpVerbs.Get, async context =>
            {
                var settings = await machineRuntimeService.GetSettingsAsync(cancellationToken);
                var commands = await remoteCommandJournal.GetAllAsync(cancellationToken);
                await context.SendDataAsync(new
                {
                    status = "online",
                    timestampUtc = DateTimeOffset.UtcNow,
                    runtimeMode = settings.RuntimeMode.ToString(),
                    databaseInitialized = true,
                    pendingRemoteCommands = commands.Count(item => item.Status == "running"),
                    lastCloudSyncUtc = cloudBridgeService.LastSuccessfulSyncUtc,
                    httpsEnabled = _httpsServer is not null,
                });
            }))
            .WithModule(new ActionModule("/api/pairing/claim", HttpVerbs.Post, async context =>
            {
                var request = await ReadJsonAsync<PairingClaimRequest>(context, cancellationToken);
                await context.SendDataAsync(await machineRuntimeService.ClaimPairingAsync(request, cancellationToken));
            }))
            .WithModule(new ActionModule("/api/device/status", HttpVerbs.Get, async context =>
            {
                if (!await RequireCompanionAccessAsync(context, cancellationToken))
                {
                    return;
                }

                var snapshot = await machineRuntimeService.GetStatusAsync(cancellationToken);
                await context.SendDataAsync(MachineSnapshotSanitizer.ForExternalApi(snapshot));
            }))
            .WithModule(new ActionModule("/api/device/dashboard", HttpVerbs.Get, async context =>
            {
                if (!await RequireCompanionAccessAsync(context, cancellationToken))
                {
                    return;
                }

                var dashboard = await machineRuntimeService.GetDashboardAsync(cancellationToken);
                await context.SendDataAsync(MachineSnapshotSanitizer.ForExternalApi(dashboard));
            }))
            .WithModule(new ActionModule("/api/device/settings", HttpVerbs.Get, async context =>
            {
                if (!await RequireCompanionAccessAsync(context, cancellationToken))
                {
                    return;
                }

                var settings = await machineRuntimeService.GetSettingsAsync(cancellationToken);
                await context.SendDataAsync(MachineSnapshotSanitizer.ForExternalApi(settings));
            }))
            .WithModule(new ActionModule("/api/device/settings", HttpVerbs.Put, async context =>
            {
                if (!await RequireCompanionAccessAsync(context, cancellationToken))
                {
                    return;
                }

                var settings = await ReadJsonAsync<MachineSettings>(context, cancellationToken);
                await machineRuntimeService.SaveSettingsAsync(settings, cancellationToken);
                var updatedSettings = await machineRuntimeService.GetSettingsAsync(cancellationToken);
                await context.SendDataAsync(MachineSnapshotSanitizer.ForExternalApi(updatedSettings));
            }))
            .WithModule(new ActionModule("/api/device/sanitation", HttpVerbs.Post, async context =>
            {
                if (!await RequireCompanionAccessAsync(context, cancellationToken))
                {
                    return;
                }

                var request = await ReadJsonAsync<SanitationRequest>(context, cancellationToken);
                await machineRuntimeService.RunSanitationAsync(request, cancellationToken);
                await context.SendDataAsync(new
                {
                    status = "accepted",
                });
            }))
            .WithModule(new ActionModule("/api/device/esp32/update", HttpVerbs.Post, async context =>
            {
                if (!await RequireCompanionAccessAsync(context, cancellationToken))
                {
                    return;
                }

                var request = await ReadJsonAsync<Esp32FirmwareUpdateRequest>(context, cancellationToken);
                await machineRuntimeService.UpdateEsp32FirmwareAsync(request, cancellationToken);
                await context.SendDataAsync(new
                {
                    status = "accepted",
                    transport = "ota",
                    firmwareUrl = request.FirmwareUrl,
                });
            }))
            .WithModule(new ActionModule("/api/device/credit", HttpVerbs.Post, async context =>
            {
                if (!await RequireCompanionAccessAsync(context, cancellationToken))
                {
                    return;
                }

                var request = await ReadJsonAsync<RemoteCreditRequest>(context, cancellationToken);
                await machineRuntimeService.AddRemoteCreditAsync(request, cancellationToken);
                var snapshot = await machineRuntimeService.GetStatusAsync(cancellationToken);
                await context.SendDataAsync(MachineSnapshotSanitizer.ForExternalApi(snapshot));
            }))
            .WithModule(new ActionModule("/api/pairing/current", HttpVerbs.Get, async context =>
            {
                if (!await RequireCompanionAccessAsync(context, cancellationToken))
                {
                    return;
                }

                await context.SendDataAsync(await machineRuntimeService.GeneratePairingAsync(cancellationToken));
            }));
    }

    private static async Task<T> ReadJsonAsync<T>(IHttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var body = await context.GetRequestBodyAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Cererea nu contine JSON.");
        }

        return JsonSerializer.Deserialize<T>(body, JsonOptions)
            ?? throw new InvalidOperationException("Nu am putut deserializa payload-ul JSON.");
    }

    private async Task<bool> RequireCompanionAccessAsync(IHttpContext context, CancellationToken cancellationToken)
    {
        var accessToken = context.Request.Headers[CompanionTokenHeaderName];
        if (await machineRuntimeService.ValidateCompanionAccessTokenAsync(accessToken, cancellationToken))
        {
            return true;
        }

        context.Response.StatusCode = 401;
        await context.SendDataAsync(new
        {
            error = "unauthorized",
        });

        return false;
    }
}
