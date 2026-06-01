using System.Net.WebSockets;
using Vendomat.Controller.Cloud.Data;
using Vendomat.Controller.Cloud.Services;
using Vendomat.Controller.Application.Contracts;
using Vendomat.Controller.Domain.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CloudStore>();
builder.Services.AddSingleton<CloudTunnelBroker>();
builder.Services.AddSingleton<PairingClaimRateLimiter>();
builder.Services.AddOpenApi();

var app = builder.Build();

var configuredPathBase = builder.Configuration["Cloud:PathBase"]?.Trim();
if (!string.IsNullOrWhiteSpace(configuredPathBase))
{
    app.UsePathBase(configuredPathBase);
}

app.UseWebSockets();
app.MapOpenApi();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (InvalidOperationException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new
        {
            error = ex.Message,
        });
    }
});

app.Map("/ws/machine", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var machineToken = context.Request.Headers["X-Vendomat-Machine-Token"].ToString();
    if (!Guid.TryParse(context.Request.Headers["X-Vendomat-Machine-Id"].ToString(), out var machineId)
        || string.IsNullOrWhiteSpace(machineToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        await context.RequestServices
            .GetRequiredService<CloudTunnelBroker>()
            .RunMachineSessionAsync(socket, machineId, machineToken, context.RequestAborted);
    }
    catch (InvalidOperationException ex)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, ex.Message, context.RequestAborted);
        }
    }
});

app.Map("/ws/companion", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var token = GetCompanionToken(context.Request);
    if (token is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    try
    {
        await context.RequestServices
            .GetRequiredService<CloudTunnelBroker>()
            .RunCompanionSessionAsync(socket, token, context.RequestAborted);
    }
    catch (InvalidOperationException ex)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, ex.Message, context.RequestAborted);
        }
    }
});

app.MapGet("/", async (CloudStore store, CancellationToken cancellationToken) =>
{
    await store.InitializeAsync(cancellationToken);
    return Results.Ok(new
    {
        service = "Vendomat.Controller.Cloud",
        status = "online",
        timestampUtc = DateTimeOffset.UtcNow,
    });
});

app.MapGet("/api/health", async (CloudStore store, CancellationToken cancellationToken) =>
{
    await store.InitializeAsync(cancellationToken);
    return Results.Ok(await store.GetOperationalHealthAsync(cancellationToken));
});

app.MapPost("/api/cloud/machine/pairing", async (CloudPairingUpsertRequest request, CloudStore store, CancellationToken cancellationToken) =>
{
    await store.UpsertPairingSessionAsync(request, cancellationToken);
    return Results.Ok(new { status = "accepted" });
});

app.MapPost("/api/cloud/machine/sync", async (CloudMachineSyncRequest request, CloudStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.SyncMachineAsync(request, cancellationToken)));

app.MapPost("/api/cloud/machine/snapshot", async (CloudMachineSyncRequest request, CloudStore store, CancellationToken cancellationToken) =>
    Results.Ok(new
    {
        hasActiveWatcher = await store.UpdateMachineSnapshotAsync(request, cancellationToken),
        timestampUtc = DateTimeOffset.UtcNow,
    }));

app.MapPost("/api/cloud/machine/commands/complete", async (CloudCommandCompletionRequest request, CloudStore store, CancellationToken cancellationToken) =>
{
    await store.CompleteCommandAsync(request, cancellationToken);
    return Results.Ok(new { status = "accepted" });
});

app.MapPost("/api/cloud/machine/companions", async (CloudMachineCompanionSessionsRequest request, CloudStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetCompanionSessionsAsync(request.MachineId, request.MachineToken, cancellationToken)));

app.MapPost("/api/cloud/machine/companions/revoke", async (CloudCompanionSessionRevokeRequest request, CloudStore store, CancellationToken cancellationToken) =>
    Results.Ok(new
    {
        revokedCount = await store.RevokeCompanionSessionsAsync(
            request.MachineId,
            request.MachineToken,
            request.CompanionTokenPrefix,
            cancellationToken),
    }));

app.MapPost("/api/cloud/machine/companions/revoke-all", async (CloudMachineCompanionSessionsRequest request, CloudStore store, CancellationToken cancellationToken) =>
    Results.Ok(new
    {
        revokedCount = await store.RevokeCompanionSessionsAsync(
            request.MachineId,
            request.MachineToken,
            companionTokenPrefix: null,
            cancellationToken),
    }));

app.MapPost("/api/pairing/claim", async Task<IResult> (
    HttpRequest httpRequest,
    PairingClaimRequest request,
    CloudStore store,
    PairingClaimRateLimiter rateLimiter,
    CancellationToken cancellationToken) =>
{
    var currentBaseUrl = $"{httpRequest.Scheme}://{httpRequest.Host}{httpRequest.PathBase}";
    var remoteAddress = httpRequest.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!rateLimiter.IsAllowed(remoteAddress, request.MachineId, out var retryAfter))
    {
        return Results.Json(new
        {
            error = "pairing_locked",
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)),
        }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    try
    {
        var result = await store.ClaimPairingAsync(request, currentBaseUrl, cancellationToken);
        rateLimiter.RecordSuccess(remoteAddress, request.MachineId);
        return Results.Ok(result);
    }
    catch (InvalidOperationException)
    {
        rateLimiter.RecordFailure(remoteAddress, request.MachineId);
        throw;
    }
});

app.MapGet("/api/device/status", async (HttpRequest request, CloudStore store, CancellationToken cancellationToken) =>
{
    var token = GetCompanionToken(request);
    return token is null
        ? Results.Unauthorized()
        : Results.Ok(await store.GetStatusAsync(token, cancellationToken));
});

app.MapGet("/api/device/dashboard", async (HttpRequest request, CloudStore store, CancellationToken cancellationToken) =>
{
    var token = GetCompanionToken(request);
    return token is null
        ? Results.Unauthorized()
        : Results.Ok(await store.GetDashboardAsync(token, cancellationToken));
});

app.MapGet("/api/device/settings", async (HttpRequest request, CloudStore store, CancellationToken cancellationToken) =>
{
    var token = GetCompanionToken(request);
    return token is null
        ? Results.Unauthorized()
        : Results.Ok(await store.GetSettingsAsync(token, cancellationToken));
});

app.MapPut("/api/device/settings", async (HttpRequest request, MachineSettings settings, CloudStore store, CancellationToken cancellationToken) =>
{
    var token = GetCompanionToken(request);
    return token is null
        ? Results.Unauthorized()
        : Results.Ok(await store.QueueSettingsAsync(token, settings, cancellationToken));
});

app.MapPost("/api/device/sanitation", async (HttpRequest request, SanitationRequest sanitationRequest, CloudStore store, CancellationToken cancellationToken) =>
{
    var token = GetCompanionToken(request);
    if (token is null)
    {
        return Results.Unauthorized();
    }

    var commandId = await store.QueueSanitationAsync(token, sanitationRequest, cancellationToken);
    return Results.Ok(new
    {
        status = "accepted",
        commandId,
    });
});

app.MapPost("/api/device/credit", async (HttpRequest request, RemoteCreditRequest creditRequest, CloudStore store, CancellationToken cancellationToken) =>
{
    var token = GetCompanionToken(request);
    if (token is null)
    {
        return Results.Unauthorized();
    }

    var commandId = await store.QueueCreditAsync(token, creditRequest.Amount, cancellationToken);
    return Results.Ok(new
    {
        status = "accepted",
        commandId,
    });
});

app.Run();

static string? GetCompanionToken(HttpRequest request)
{
    if (!request.Headers.TryGetValue("X-Vendomat-Token", out var values))
    {
        return null;
    }

    var token = values.ToString()?.Trim();
    return string.IsNullOrWhiteSpace(token) ? null : token;
}
