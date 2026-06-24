using System.Net.Http;
using System.Net.Http.Json;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.IntegrationTests.Infrastructure;

/// <summary>Builders for the JSON payloads exchanged with the Cloud server.</summary>
public static class TestData
{
    public static CloudMachineSyncRequest SyncRequest(
        MachineCredentials creds,
        decimal pricePerLiter = 10m,
        decimal stockLiters = 120m,
        DateTimeOffset? generatedAtUtc = null,
        MachineDashboardSnapshot? dashboard = null)
    {
        var settings = new MachineSettings
        {
            MachineId = creds.MachineId,
            MachineName = "Test Vendomat",
            PricePerLiter = pricePerLiter,
            CurrentStockLiters = stockLiters,
            TankCapacityLiters = 200m,
            LowStockThresholdLiters = 20m,
        };

        return new CloudMachineSyncRequest
        {
            MachineId = creds.MachineId,
            MachineName = "Test Vendomat",
            MachineToken = creds.MachineToken,
            CompanionAccessToken = creds.CompanionToken,
            LocalApiBaseUrl = "http://vendomat.local:1326",
            PublicApiBaseUrl = string.Empty,
            CloudApiBaseUrl = "https://vending.example.test",
            Snapshot = new MachineStatusSnapshot
            {
                Settings = settings,
                GeneratedAtUtc = generatedAtUtc ?? DateTimeOffset.UtcNow,
            },
            Dashboard = dashboard,
        };
    }

    /// <summary>A dashboard carrying <paramref name="saleCount"/> completed sales (for bulk tests).</summary>
    public static MachineDashboardSnapshot DashboardWithSales(MachineCredentials creds, int saleCount, decimal pricePerLiter = 10m)
    {
        var sales = new List<SaleTransaction>();
        decimal totalRevenue = 0m;
        for (var i = 0; i < saleCount; i++)
        {
            var liters = 1m + i % 3;
            var amount = liters * pricePerLiter;
            totalRevenue += amount;
            sales.Add(new SaleTransaction
            {
                Id = Guid.NewGuid(),
                MachineId = creds.MachineId,
                StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-saleCount + i),
                CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-saleCount + i),
                RequestedLiters = liters,
                DispensedLiters = liters,
                PricePerLiter = pricePerLiter,
                TotalAmount = amount,
                Status = Vendomat.Controller.Domain.Enums.SaleStatus.Completed,
            });
        }

        return new MachineDashboardSnapshot
        {
            Status = new MachineStatusSnapshot
            {
                Settings = new MachineSettings { MachineId = creds.MachineId, PricePerLiter = pricePerLiter },
            },
            Sales = new SalesDashboardSummary
            {
                TotalCompletedSales = saleCount,
                TotalRevenue = totalRevenue,
                TodayCompletedSales = saleCount,
                TodayRevenue = totalRevenue,
            },
            RecentSales = sales,
        };
    }

    /// <summary>
    /// Runs the full onboarding flow exactly as the real system does: the machine registers and
    /// opens a pairing session carrying its raw companion token, then the operator claims that
    /// pairing to obtain a usable companion bearer token (creating a companion session row).
    /// </summary>
    public static async Task RegisterAndPairAsync(
        HttpClient client,
        MachineCredentials creds,
        decimal pricePerLiter = 10m,
        decimal stockLiters = 120m)
    {
        using (var sync = await client.PostAsJsonAsync("/api/cloud/machine/sync", SyncRequest(creds, pricePerLiter, stockLiters)))
        {
            sync.EnsureSuccessStatusCode();
        }

        var pairingCode = Guid.NewGuid().ToString("N");
        var upsert = new
        {
            machineId = creds.MachineId,
            machineName = "Test Vendomat",
            machineToken = creds.MachineToken,
            companionAccessToken = creds.CompanionToken,
            localApiBaseUrl = "http://vendomat.local:1326",
            cloudApiBaseUrl = "https://vending.example.test",
            pairingCode,
            expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        using (var p = await client.PostAsJsonAsync("/api/cloud/machine/pairing", upsert))
        {
            p.EnsureSuccessStatusCode();
        }

        using var claim = await client.PostAsJsonAsync(
            "/api/pairing/claim", new { machineId = creds.MachineId, pairingCode });
        claim.EnsureSuccessStatusCode();
    }

    /// <summary>Issues a device (operator) request carrying the companion bearer token.</summary>
    public static HttpRequestMessage DeviceRequest(HttpMethod method, string path, string companionToken, object? body = null)
    {
        var message = new HttpRequestMessage(method, path);
        message.Headers.Add("X-Vendomat-Token", companionToken);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }

        return message;
    }
}
