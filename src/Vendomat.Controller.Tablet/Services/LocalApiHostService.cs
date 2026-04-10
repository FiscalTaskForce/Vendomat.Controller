using System.Text.Json;
using Android.Util;
using EmbedIO;
using EmbedIO.Actions;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Tablet.Services;

public sealed class LocalApiHostService(IMachineRuntimeService machineRuntimeService) : ILocalApiHost
{
    private const string StartupTag = "VendomatStartup";
    private const string CompanionTokenHeaderName = "X-Vendomat-Token";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private WebServer? _server;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_server is not null)
            {
                return;
            }

            Log.Info(StartupTag, "Configuring local API host");
            _server = new WebServer(options => options
                    .WithUrlPrefix("http://*:1326/")
                    .WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/api/health", HttpVerbs.Get, async context =>
                {
                    await context.SendDataAsync(new
                    {
                        status = "online",
                        timestampUtc = DateTimeOffset.UtcNow,
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

                    await context.SendDataAsync(await machineRuntimeService.GetStatusAsync(cancellationToken));
                }))
                .WithModule(new ActionModule("/api/device/dashboard", HttpVerbs.Get, async context =>
                {
                    if (!await RequireCompanionAccessAsync(context, cancellationToken))
                    {
                        return;
                    }

                    await context.SendDataAsync(await machineRuntimeService.GetDashboardAsync(cancellationToken));
                }))
                .WithModule(new ActionModule("/api/device/settings", HttpVerbs.Get, async context =>
                {
                    if (!await RequireCompanionAccessAsync(context, cancellationToken))
                    {
                        return;
                    }

                    await context.SendDataAsync(await machineRuntimeService.GetSettingsAsync(cancellationToken));
                }))
                .WithModule(new ActionModule("/api/device/settings", HttpVerbs.Put, async context =>
                {
                    if (!await RequireCompanionAccessAsync(context, cancellationToken))
                    {
                        return;
                    }

                    var settings = await ReadJsonAsync<MachineSettings>(context, cancellationToken);
                    await machineRuntimeService.SaveSettingsAsync(settings, cancellationToken);
                    await context.SendDataAsync(await machineRuntimeService.GetSettingsAsync(cancellationToken));
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
                .WithModule(new ActionModule("/api/device/credit", HttpVerbs.Post, async context =>
                {
                    if (!await RequireCompanionAccessAsync(context, cancellationToken))
                    {
                        return;
                    }

                    var request = await ReadJsonAsync<RemoteCreditRequest>(context, cancellationToken);
                    await machineRuntimeService.AddRemoteCreditAsync(request.Amount, cancellationToken);
                    await context.SendDataAsync(await machineRuntimeService.GetStatusAsync(cancellationToken));
                }))
                .WithModule(new ActionModule("/api/pairing/current", HttpVerbs.Get, async context =>
                {
                    if (!await RequireCompanionAccessAsync(context, cancellationToken))
                    {
                        return;
                    }

                    await context.SendDataAsync(await machineRuntimeService.GeneratePairingAsync(cancellationToken));
                }));

            await Task.Run(() =>
            {
                Log.Info(StartupTag, "Starting local API host listener");
                _server.Start();
            }, cancellationToken);

            Log.Info(StartupTag, "Local API host listener started");
        }
        finally
        {
            _startLock.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            Log.Info(StartupTag, "Stopping local API host");
        }

        _server?.Dispose();
        _server = null;
        return Task.CompletedTask;
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
