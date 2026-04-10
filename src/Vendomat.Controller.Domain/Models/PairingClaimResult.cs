namespace Vendomat.Controller.Domain.Models;

public sealed class PairingClaimResult
{
    public Guid MachineId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string LocalApiBaseUrl { get; set; } = string.Empty;
    public string PublicApiBaseUrl { get; set; } = string.Empty;
    public string CloudApiBaseUrl { get; set; } = string.Empty;
    public string CompanionAccessToken { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
