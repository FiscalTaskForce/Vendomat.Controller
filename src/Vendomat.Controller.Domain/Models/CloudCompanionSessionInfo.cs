namespace Vendomat.Controller.Domain.Models;

public sealed class CloudCompanionSessionInfo
{
    public string CompanionTokenPrefix { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset LastUsedUtc { get; set; }
}
