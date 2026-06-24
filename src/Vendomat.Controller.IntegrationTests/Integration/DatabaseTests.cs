using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.IntegrationTests.Infrastructure;
using Xunit;

namespace Vendomat.Controller.IntegrationTests.Integration;

/// <summary>REST API ↔ SQLite integration: writes through the API must be readable back.</summary>
[Collection("cloud")]
public sealed class DatabaseTests(CloudServerFixture fixture)
{
    private readonly CloudServerFixture _fixture = fixture;

    [Fact]
    public async Task Machine_sync_persists_and_status_is_readable()
    {
        var creds = await _fixture.RegisterMachineAsync(pricePerLiter: 9.25m);

        using var request = TestData.DeviceRequest(HttpMethod.Get, "/api/device/status", creds.CompanionToken);
        using var resp = await _fixture.Client.SendAsync(request);
        resp.EnsureSuccessStatusCode();

        var status = await resp.Content.ReadFromJsonAsync<MachineStatusSnapshot>();
        Assert.NotNull(status);
        Assert.Equal(9.25m, status!.Settings.PricePerLiter);
    }

    [Fact]
    public async Task Operator_settings_update_is_queued_as_pending_command()
    {
        var creds = await _fixture.RegisterMachineAsync();

        var newSettings = new MachineSettings { MachineId = creds.MachineId, PricePerLiter = 15m };
        using var put = TestData.DeviceRequest(HttpMethod.Put, "/api/device/settings", creds.CompanionToken, newSettings);
        using var putResp = await _fixture.Client.SendAsync(put);
        putResp.EnsureSuccessStatusCode();

        var sync = await SyncAsync(creds);
        Assert.Contains(sync.PendingCommands, c => c.CommandType == CloudCommandTypes.SaveSettings);
    }

    [Fact]
    public async Task Updating_a_record_overwrites_the_previous_value()
    {
        var creds = await _fixture.RegisterMachineAsync(pricePerLiter: 10m);

        // Re-sync the same machine with a changed value (an UPDATE, not an INSERT).
        var updated = TestData.SyncRequest(creds, pricePerLiter: 11m);
        using var resp = await _fixture.Client.PostAsJsonAsync("/api/cloud/machine/sync", updated);
        resp.EnsureSuccessStatusCode();

        using var read = TestData.DeviceRequest(HttpMethod.Get, "/api/device/settings", creds.CompanionToken);
        var settings = await (await _fixture.Client.SendAsync(read)).Content.ReadFromJsonAsync<MachineSettings>();
        Assert.Equal(11m, settings!.PricePerLiter);
    }

    [Fact]
    public async Task Health_machine_count_reflects_registrations()
    {
        var before = (await Health()).GetProperty("machineCount").GetInt32();
        await _fixture.RegisterMachineAsync();
        var after = (await Health()).GetProperty("machineCount").GetInt32();
        Assert.True(after >= before + 1);
    }

    private async Task<CloudMachineSyncResult> SyncAsync(MachineCredentials creds)
    {
        var request = TestData.SyncRequest(creds);
        using var resp = await _fixture.Client.PostAsJsonAsync("/api/cloud/machine/sync", request);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CloudMachineSyncResult>())!;
    }

    private async Task<JsonElement> Health() =>
        await _fixture.Client.GetFromJsonAsync<JsonElement>("/api/health");
}
