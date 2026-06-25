using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Vendomat.Controller.Cloud.Data;
using Vendomat.Controller.Cloud.Services;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Security;
using Xunit;

namespace Vendomat.Controller.Cloud.Tests;

public sealed class CloudStoreSecurityTests
{
    [Fact]
    public async Task PairingStoresOnlyHashedTokensInDatabase()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"vendomat-cloud-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "cloud.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cloud:DatabasePath"] = databasePath,
            })
            .Build();

        var store = new CloudStore(new TestHostEnvironment(tempDirectory), configuration);
        var machineId = Guid.NewGuid();
        const string machineToken = "MACHINE_TOKEN_0123456789";
        const string companionToken = "COMPANION_TOKEN_0123456789";

        await store.UpsertPairingSessionAsync(new CloudPairingUpsertRequest
        {
            MachineId = machineId,
            MachineName = "test",
            MachineToken = machineToken,
            CompanionAccessToken = companionToken,
            CloudApiBaseUrl = "https://cloud.example.test",
            PairingCode = "123456",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        });

        var claim = await store.ClaimPairingAsync(new PairingClaimRequest
        {
            MachineId = machineId,
            PairingCode = "123456",
        }, "https://cloud.example.test");

        Assert.Equal(companionToken, claim.CompanionAccessToken);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString());
        await connection.OpenAsync();

        var storedTokens = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT MachineToken FROM machines
                UNION ALL
                SELECT CompanionAccessToken FROM machines
                UNION ALL
                SELECT CompanionAccessToken FROM companion_sessions;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                storedTokens.Add(reader.GetString(0));
            }
        }

        Assert.All(storedTokens, token =>
        {
            Assert.DoesNotContain(machineToken, token, StringComparison.Ordinal);
            Assert.DoesNotContain(companionToken, token, StringComparison.Ordinal);
            Assert.True(CompanionAccessTokenSecurity.IsStoredHash(token));
        });
    }

    [Fact]
    public async Task PairingSelfRegistersMachineWhenAutoRegisterDisabled()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"vendomat-cloud-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "cloud.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cloud:DatabasePath"] = databasePath,
                ["Cloud:AllowMachineAutoRegister"] = "false",
            })
            .Build();

        var store = new CloudStore(new TestHostEnvironment(tempDirectory), configuration);
        var machineId = Guid.NewGuid();
        const string machineToken = "MACHINE_TOKEN_0123456789";
        const string companionToken = "COMPANION_TOKEN_0123456789";

        // A brand-new machine has no row yet; publishing its pairing must self-register it
        // even though auto-registration is disabled, otherwise the QR flow can never pair.
        await store.UpsertPairingSessionAsync(new CloudPairingUpsertRequest
        {
            MachineId = machineId,
            MachineName = "test",
            MachineToken = machineToken,
            CompanionAccessToken = companionToken,
            CloudApiBaseUrl = "https://cloud.example.test",
            PairingCode = "123456",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        });

        var claim = await store.ClaimPairingAsync(new PairingClaimRequest
        {
            MachineId = machineId,
            PairingCode = "123456",
        }, "https://cloud.example.test");

        Assert.Equal(companionToken, claim.CompanionAccessToken);
    }

    [Fact]
    public async Task SyncRejectsUnregisteredMachineWhenAutoRegisterDisabled()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"vendomat-cloud-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "cloud.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cloud:DatabasePath"] = databasePath,
                ["Cloud:AllowMachineAutoRegister"] = "false",
            })
            .Build();

        var store = new CloudStore(new TestHostEnvironment(tempDirectory), configuration);

        // Only the pairing-publish path may self-register. Sync stays strict so an
        // unknown machine cannot create an identity by syncing.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SyncMachineAsync(new CloudMachineSyncRequest
            {
                MachineId = Guid.NewGuid(),
                MachineName = "test",
                MachineToken = "MACHINE_TOKEN_0123456789",
                CompanionAccessToken = "COMPANION_TOKEN_0123456789",
                Snapshot = new MachineStatusSnapshot(),
            }));
        Assert.Contains("not registered", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PairingRateLimiterLocksAfterRepeatedFailures()
    {
        var limiter = new PairingClaimRateLimiter();
        var machineId = Guid.NewGuid();
        const string remoteAddress = "127.0.0.1";

        for (var index = 0; index < 5; index++)
        {
            Assert.True(limiter.IsAllowed(remoteAddress, machineId, out _));
            limiter.RecordFailure(remoteAddress, machineId);
        }

        Assert.False(limiter.IsAllowed(remoteAddress, machineId, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task SyncStoresSanitizedSnapshots()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"vendomat-cloud-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "cloud.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cloud:DatabasePath"] = databasePath,
            })
            .Build();

        var store = new CloudStore(new TestHostEnvironment(tempDirectory), configuration);
        var machineId = Guid.NewGuid();
        const string machineToken = "MACHINE_TOKEN_0123456789";
        const string companionToken = "COMPANION_TOKEN_0123456789";
        var adminHash = AdminPasscodeHasher.Hash("9876");

        await store.SyncMachineAsync(new CloudMachineSyncRequest
        {
            MachineId = machineId,
            MachineName = "test",
            MachineToken = machineToken,
            CompanionAccessToken = companionToken,
            Snapshot = new MachineStatusSnapshot
            {
                Settings = new MachineSettings
                {
                    MachineId = machineId,
                    MachineName = "test",
                    CloudMachineToken = machineToken,
                    CompanionAccessToken = companionToken,
                    AdminPasscodeHash = adminHash,
                },
            },
            Dashboard = new MachineDashboardSnapshot
            {
                Status = new MachineStatusSnapshot
                {
                    Settings = new MachineSettings
                    {
                        MachineId = machineId,
                        MachineName = "test",
                        CloudMachineToken = machineToken,
                        CompanionAccessToken = companionToken,
                        AdminPasscodeHash = adminHash,
                    },
                },
            },
        });

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString());
        await connection.OpenAsync();

        string statusJson;
        string settingsJson;
        string dashboardJson;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT LastStatusJson, LastSettingsJson, LastDashboardJson FROM machines WHERE MachineId = @MachineId;";
            command.Parameters.AddWithValue("@MachineId", machineId.ToString("N"));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            statusJson = reader.GetString(0);
            settingsJson = reader.GetString(1);
            dashboardJson = reader.GetString(2);
        }

        Assert.DoesNotContain(machineToken, statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain(companionToken, statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain(adminHash, statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain(machineToken, settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(companionToken, settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(adminHash, settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(machineToken, dashboardJson, StringComparison.Ordinal);
        Assert.DoesNotContain(companionToken, dashboardJson, StringComparison.Ordinal);
        Assert.DoesNotContain(adminHash, dashboardJson, StringComparison.Ordinal);

        var storedSnapshot = JsonSerializer.Deserialize<MachineStatusSnapshot>(statusJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(storedSnapshot);
        Assert.Empty(storedSnapshot!.Settings.CloudMachineToken);
        Assert.Empty(storedSnapshot.Settings.CompanionAccessToken);
        Assert.Empty(storedSnapshot.Settings.AdminPasscodeHash);
    }

    [Fact]
    public async Task CompanionSessionsCanBeListedAndRevoked()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"vendomat-cloud-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "cloud.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cloud:DatabasePath"] = databasePath,
            })
            .Build();

        var store = new CloudStore(new TestHostEnvironment(tempDirectory), configuration);
        var machineId = Guid.NewGuid();
        const string machineToken = "MACHINE_TOKEN_0123456789";
        const string companionToken = "COMPANION_TOKEN_0123456789";

        await store.UpsertPairingSessionAsync(new CloudPairingUpsertRequest
        {
            MachineId = machineId,
            MachineName = "test",
            MachineToken = machineToken,
            CompanionAccessToken = companionToken,
            CloudApiBaseUrl = "https://cloud.example.test",
            PairingCode = "123456",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        });

        await store.ClaimPairingAsync(new PairingClaimRequest
        {
            MachineId = machineId,
            PairingCode = "123456",
        }, "https://cloud.example.test");

        var sessions = await store.GetCompanionSessionsAsync(machineId, machineToken);
        var session = Assert.Single(sessions);
        Assert.False(string.IsNullOrWhiteSpace(session.CompanionTokenPrefix));

        var revokedCount = await store.RevokeCompanionSessionsAsync(machineId, machineToken, session.CompanionTokenPrefix);
        Assert.Equal(1, revokedCount);
        Assert.Empty(await store.GetCompanionSessionsAsync(machineId, machineToken));
    }

    [Fact]
    public async Task CachedStatusExpiresUsingCloudReceiptTime()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"vendomat-cloud-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "cloud.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cloud:DatabasePath"] = databasePath,
                ["Cloud:MachineOfflineAfterSeconds"] = "30",
            })
            .Build();

        var store = new CloudStore(new TestHostEnvironment(tempDirectory), configuration);
        var machineId = Guid.NewGuid();
        const string machineToken = "MACHINE_TOKEN_0123456789";
        const string companionToken = "COMPANION_TOKEN_0123456789";

        await store.SyncMachineAsync(new CloudMachineSyncRequest
        {
            MachineId = machineId,
            MachineName = "test",
            MachineToken = machineToken,
            CompanionAccessToken = companionToken,
            Snapshot = new MachineStatusSnapshot
            {
                Settings = new MachineSettings
                {
                    MachineId = machineId,
                    MachineName = "test",
                },
                GeneratedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            },
            Dashboard = new MachineDashboardSnapshot
            {
                Status = new MachineStatusSnapshot
                {
                    Settings = new MachineSettings
                    {
                        MachineId = machineId,
                        MachineName = "test",
                    },
                },
            },
        });

        await store.UpsertPairingSessionAsync(new CloudPairingUpsertRequest
        {
            MachineId = machineId,
            MachineName = "test",
            MachineToken = machineToken,
            CompanionAccessToken = companionToken,
            CloudApiBaseUrl = "https://cloud.example.test",
            PairingCode = "123456",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        });

        await store.ClaimPairingAsync(new PairingClaimRequest
        {
            MachineId = machineId,
            PairingCode = "123456",
        }, "https://cloud.example.test");

        var freshStatus = await store.GetStatusAsync(companionToken);
        Assert.Equal(machineId, freshStatus.Settings.MachineId);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE machines SET LastSeenUtc = @LastSeenUtc WHERE MachineId = @MachineId;";
            command.Parameters.AddWithValue("@LastSeenUtc", DateTimeOffset.UtcNow.AddMinutes(-5).UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("@MachineId", machineId.ToString("N"));
            await command.ExecuteNonQueryAsync();
        }

        var statusError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetStatusAsync(companionToken));
        Assert.Contains("offline", statusError.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetDashboardAsync(companionToken));
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Vendomat.Controller.Cloud.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
