using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Vendomat.Controller.IntegrationTests.Infrastructure;
using Xunit;

namespace Vendomat.Controller.IntegrationTests.Security;

/// <summary>Security tests: authentication, input validation and injection resistance.</summary>
[Collection("cloud")]
public sealed class SecurityTests(CloudServerFixture fixture)
{
    private readonly CloudServerFixture _fixture = fixture;

    [Fact]
    public async Task Request_without_token_is_unauthorized()
    {
        using var resp = await _fixture.Client.GetAsync("/api/device/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Invalid_token_is_rejected_without_server_error()
    {
        using var request = TestData.DeviceRequest(HttpMethod.Get, "/api/device/status", "totally-bogus-token");
        using var resp = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.True((int)resp.StatusCode < 500, "must not leak a 5xx for an invalid token");
    }

    [Fact]
    public async Task Negative_credit_amount_is_rejected()
    {
        var creds = await _fixture.RegisterMachineAsync();
        using var request = TestData.DeviceRequest(HttpMethod.Post, "/api/device/credit", creds.CompanionToken, new { amount = -50m });
        using var resp = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Sql_injection_in_token_neither_authorizes_nor_corrupts_the_database()
    {
        var before = (await Health()).GetProperty("machineCount").GetInt32();

        const string payload = "x'; DROP TABLE machines;-- ";
        using var request = TestData.DeviceRequest(HttpMethod.Get, "/api/device/status", payload);
        using var resp = await _fixture.Client.SendAsync(request);

        Assert.True(resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized);

        // The machines table must still exist and be intact (health still queries it).
        var after = (await Health()).GetProperty("machineCount").GetInt32();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Malformed_json_is_handled_gracefully()
    {
        using var content = new StringContent("{ this is not valid json ", Encoding.UTF8, "application/json");
        using var resp = await _fixture.Client.PostAsync("/api/cloud/machine/sync", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // Server is still alive afterwards.
        Assert.Equal("online", (await Health()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Oversized_payload_does_not_crash_the_server()
    {
        var huge = new string('A', 2 * 1024 * 1024); // 2 MB pairing code
        using var resp = await _fixture.Client.PostAsJsonAsync(
            "/api/pairing/claim", new { machineId = Guid.NewGuid(), pairingCode = huge });

        Assert.True((int)resp.StatusCode < 500, $"large input should not 5xx (was {(int)resp.StatusCode})");
        Assert.Equal("online", (await Health()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unregistered_machine_cannot_complete_commands()
    {
        var body = new
        {
            machineId = Guid.NewGuid(),
            machineToken = "mt_unknown",
            commandId = Guid.NewGuid(),
            success = true,
        };
        using var resp = await _fixture.Client.PostAsJsonAsync("/api/cloud/machine/commands/complete", body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private async Task<JsonElement> Health() =>
        await _fixture.Client.GetFromJsonAsync<JsonElement>("/api/health");
}
