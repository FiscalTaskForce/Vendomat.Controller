using System.Net.WebSockets;
using Vendomat.Controller.IntegrationTests.Infrastructure;
using Xunit;

namespace Vendomat.Controller.IntegrationTests.Integration;

/// <summary>WebSocket ↔ server integration: real-time tunnel handshake and authentication.</summary>
[Collection("cloud")]
public sealed class WebSocketTests(CloudServerFixture fixture)
{
    private readonly CloudServerFixture _fixture = fixture;

    [Fact]
    public async Task Machine_tunnel_rejects_connection_without_credentials()
    {
        using var ws = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(
            () => ws.ConnectAsync(new Uri($"{_fixture.Server.WsBaseUrl}/ws/machine"), CancellationToken.None));
    }

    [Fact]
    public async Task Companion_tunnel_rejects_connection_without_token()
    {
        using var ws = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(
            () => ws.ConnectAsync(new Uri($"{_fixture.Server.WsBaseUrl}/ws/companion"), CancellationToken.None));
    }

    [Fact]
    public async Task Machine_tunnel_accepts_a_registered_machine()
    {
        var creds = await _fixture.RegisterMachineAsync();

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Vendomat-Machine-Id", creds.MachineId.ToString());
        ws.Options.SetRequestHeader("X-Vendomat-Machine-Token", creds.MachineToken);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(new Uri($"{_fixture.Server.WsBaseUrl}/ws/machine"), cts.Token);

        Assert.Equal(WebSocketState.Open, ws.State);

        // The tunnel handshake succeeded; closing is best-effort (the broker may tear the
        // session down first, which is normal).
        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // server already closed its side
        }
    }
}
