using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.IntegrationTests.Infrastructure;
using Xunit;

namespace Vendomat.Controller.IntegrationTests.Functional;

/// <summary>End-to-end functional scenarios from the test specification.</summary>
[Collection("cloud")]
public sealed class ScenarioTests(CloudServerFixture fixture)
{
    private readonly CloudServerFixture _fixture = fixture;

    // Scenario 1: connectivity loss and resynchronisation.
    [Fact]
    public async Task Scenario1_offline_commands_are_delivered_and_not_duplicated_on_reconnect()
    {
        var creds = await _fixture.RegisterMachineAsync();

        // Operator acts while the machine is "offline" (not currently syncing).
        using var credit = TestData.DeviceRequest(HttpMethod.Post, "/api/device/credit", creds.CompanionToken, new { amount = 7.5m });
        var creditResp = await _fixture.Client.SendAsync(credit);
        creditResp.EnsureSuccessStatusCode();

        // Machine reconnects -> the queued command is delivered.
        var firstSync = await SyncAsync(creds);
        var command = Assert.Single(firstSync.PendingCommands, c => c.CommandType == CloudCommandTypes.AddCredit);
        Assert.NotNull(command.CreditCommand);

        // Machine confirms execution.
        await CompleteAsync(creds, command.CommandId);

        // After completion the command is no longer pending (no duplicate on the next reconnect).
        var secondSync = await SyncAsync(creds);
        Assert.DoesNotContain(secondSync.PendingCommands, c => c.CommandId == command.CommandId);
    }

    // Scenario 2: synchronisation conflict -> last writer wins.
    [Fact]
    public async Task Scenario2_conflicting_updates_resolve_to_the_latest_write()
    {
        var creds = await _fixture.RegisterMachineAsync(pricePerLiter: 10m);

        var older = TestData.SyncRequest(creds, pricePerLiter: 10m, generatedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5));
        (await _fixture.Client.PostAsJsonAsync("/api/cloud/machine/sync", older)).EnsureSuccessStatusCode();

        var newer = TestData.SyncRequest(creds, pricePerLiter: 13m, generatedAtUtc: DateTimeOffset.UtcNow);
        (await _fixture.Client.PostAsJsonAsync("/api/cloud/machine/sync", newer)).EnsureSuccessStatusCode();

        using var read = TestData.DeviceRequest(HttpMethod.Get, "/api/device/settings", creds.CompanionToken);
        var settings = await (await _fixture.Client.SendAsync(read)).Content.ReadFromJsonAsync<MachineSettings>();
        Assert.Equal(13m, settings!.PricePerLiter);
    }

    // Scenario 3: large backlog of transactions synced at once.
    [Fact]
    public async Task Scenario3_bulk_transaction_backlog_syncs_without_loss()
    {
        var creds = await _fixture.RegisterMachineAsync();
        const int count = 150;
        var dashboard = TestData.DashboardWithSales(creds, count);

        var request = TestData.SyncRequest(creds, dashboard: dashboard);
        (await _fixture.Client.PostAsJsonAsync("/api/cloud/machine/sync", request)).EnsureSuccessStatusCode();

        using var read = TestData.DeviceRequest(HttpMethod.Get, "/api/device/dashboard", creds.CompanionToken);
        var stored = await (await _fixture.Client.SendAsync(read)).Content.ReadFromJsonAsync<MachineDashboardSnapshot>();

        Assert.Equal(count, stored!.RecentSales.Count);
        Assert.Equal(count, stored.Sales.TotalCompletedSales);
    }

    // Scenario 4: central server outage -> graceful failure and no data loss across a restart.
    [Fact]
    public async Task Scenario4_server_outage_loses_no_persisted_data()
    {
        // Use a database path that survives instance teardown (not auto-deleted).
        var dbPath = Path.Combine(Path.GetTempPath(), $"vendomat-outage-{Guid.NewGuid():N}", "test.db");
        var creds = MachineCredentials.New();

        try
        {
            // Server is up: register and queue a command (persisted to SQLite).
            await using (var server1 = await CloudServerProcess.StartAsync(dbPath))
            {
                using var c1 = server1.CreateClient();
                await TestData.RegisterAndPairAsync(c1, creds);
                using var credit = TestData.DeviceRequest(HttpMethod.Post, "/api/device/credit", creds.CompanionToken, new { amount = 5m });
                (await c1.SendAsync(credit)).EnsureSuccessStatusCode();

                // Confirm the command is persisted/queued without consuming it (a sync would mark it dispatched).
                var health = await c1.GetFromJsonAsync<JsonElement>("/api/health");
                Assert.True(health.GetProperty("pendingCommandCount").GetInt32() >= 1);

                // Outage: the operator's client must fail gracefully (a catchable error, not a hang/crash).
                var deadUrl = server1.BaseUrl;
                await server1.DisposeAsync();
                using var deadClient = new HttpClient { BaseAddress = new Uri(deadUrl), Timeout = TimeSpan.FromSeconds(5) };
                await Assert.ThrowsAnyAsync<HttpRequestException>(() => deadClient.GetAsync("/api/health"));
            }

            // Recovery: a new server on the same database still holds the queued command.
            await using var server2 = await CloudServerProcess.StartAsync(dbPath);
            using var c2 = server2.CreateClient();
            var afterRestart = await SyncAsync(c2, creds);
            Assert.Contains(afterRestart.PendingCommands, x => x.CommandType == CloudCommandTypes.AddCredit);
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(dbPath)!);
        }
    }

    // Scenario 5: low-stock condition is surfaced to the operator through synchronisation.
    [Fact]
    public async Task Scenario5_low_stock_state_propagates_to_the_operator()
    {
        var creds = await _fixture.RegisterMachineAsync(stockLiters: 8m); // below the 20 L threshold

        using var read = TestData.DeviceRequest(HttpMethod.Get, "/api/device/settings", creds.CompanionToken);
        var settings = await (await _fixture.Client.SendAsync(read)).Content.ReadFromJsonAsync<MachineSettings>();

        Assert.True(settings!.CurrentStockLiters <= settings.LowStockThresholdLiters, "stock should be at/under the low threshold");
        Assert.True(settings.StockFillPercent < 0.2m);
    }

    private async Task<CloudMachineSyncResult> SyncAsync(MachineCredentials creds) => await SyncAsync(_fixture.Client, creds);

    private static async Task<CloudMachineSyncResult> SyncAsync(HttpClient client, MachineCredentials creds)
    {
        using var resp = await client.PostAsJsonAsync("/api/cloud/machine/sync", TestData.SyncRequest(creds));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CloudMachineSyncResult>())!;
    }

    private async Task CompleteAsync(MachineCredentials creds, Guid commandId)
    {
        var body = new { machineId = creds.MachineId, machineToken = creds.MachineToken, commandId, success = true };
        using var resp = await _fixture.Client.PostAsJsonAsync("/api/cloud/machine/commands/complete", body);
        resp.EnsureSuccessStatusCode();
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
    }
}
