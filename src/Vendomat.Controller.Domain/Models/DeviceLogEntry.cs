using Vendomat.Controller.Domain.Enums;

namespace Vendomat.Controller.Domain.Models;

public sealed class DeviceLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public LogSeverity Severity { get; set; } = LogSeverity.Info;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}
