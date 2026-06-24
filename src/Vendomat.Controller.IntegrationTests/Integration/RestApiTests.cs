using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.IntegrationTests.Infrastructure;
using Xunit;

namespace Vendomat.Controller.IntegrationTests.Integration;

[Collection("cloud")]
public sealed class RestApiTests(CloudServerFixture fixture)
{
    private readonly CloudServerFixture _fixture = fixture;

    [Fact]
    public async Task Health_endpoint_reports_online_with_schema_version()
    {
        var json = await _fixture.Client.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("online", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("schemaVersion").GetInt32() > 0);
    }

    [Fact]
    public async Task Root_endpoint_returns_service_descriptor()
    {
        var json = await _fixture.Client.GetFromJsonAsync<JsonElement>("/");
        Assert.Equal("Vendomat.Controller.Cloud", json.GetProperty("service").GetString());
        Assert.Equal("online", json.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("/api/device/status")]
    [InlineData("/api/device/dashboard")]
    [InlineData("/api/device/settings")]
    public async Task Device_endpoints_require_authentication(string path)
    {
        using var resp = await _fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Pairing_claim_with_missing_fields_returns_400()
    {
        using var resp = await _fixture.Client.PostAsJsonAsync("/api/pairing/claim", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Json_payloads_round_trip_decimal_values_intact()
    {
        var creds = await _fixture.RegisterMachineAsync(pricePerLiter: 12.50m);

        using var request = TestData.DeviceRequest(HttpMethod.Get, "/api/device/settings", creds.CompanionToken);
        using var resp = await _fixture.Client.SendAsync(request);
        resp.EnsureSuccessStatusCode();

        var settings = await resp.Content.ReadFromJsonAsync<MachineSettings>();
        Assert.NotNull(settings);
        Assert.Equal(12.50m, settings!.PricePerLiter);
    }
}
