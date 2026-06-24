using System.Net.Http.Json;
using Vendomat.Controller.Domain.Models;
using Xunit;

namespace Vendomat.Controller.IntegrationTests.Infrastructure;

/// <summary>
/// Shared, once-per-collection running instance of the Cloud server. Mirrors the role of a
/// pytest <c>conftest.py</c> session fixture: a single setup/teardown shared by every test class
/// in the collection.
/// </summary>
public sealed class CloudServerFixture : IAsyncLifetime
{
    public CloudServerProcess Server { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Server = await CloudServerProcess.StartAsync();
        Client = Server.CreateClient();
    }

    public Task DisposeAsync()
    {
        Client?.Dispose();
        return Server is null ? Task.CompletedTask : Server.DisposeAsync().AsTask();
    }

    /// <summary>Registers and pairs a fresh machine, returning usable machine + operator credentials.</summary>
    public async Task<MachineCredentials> RegisterMachineAsync(decimal pricePerLiter = 10m, decimal stockLiters = 120m)
    {
        var creds = MachineCredentials.New();
        await TestData.RegisterAndPairAsync(Client, creds, pricePerLiter, stockLiters);
        return creds;
    }
}

[CollectionDefinition("cloud")]
public sealed class CloudCollection : ICollectionFixture<CloudServerFixture>;

/// <summary>Credentials a machine presents to the cloud, plus the operator (companion) token.</summary>
public sealed record MachineCredentials(Guid MachineId, string MachineToken, string CompanionToken)
{
    public static MachineCredentials New() => new(
        Guid.NewGuid(),
        $"mt_{Guid.NewGuid():N}",
        $"ct_{Guid.NewGuid():N}");
}
