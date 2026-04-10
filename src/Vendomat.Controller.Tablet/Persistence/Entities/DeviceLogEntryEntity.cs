using SQLite;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Tablet.Persistence.Entities;

[Table("device_logs")]
public sealed class DeviceLogEntryEntity
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public long CreatedAtUtcTicks { get; set; }
    public int Severity { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;

    public DeviceLogEntry ToDomain() => new()
    {
        Id = Guid.Parse(Id),
        CreatedAtUtc = new DateTimeOffset(CreatedAtUtcTicks, TimeSpan.Zero),
        Severity = (LogSeverity)Severity,
        Category = Category,
        Message = Message,
        Details = Details,
    };

    public static DeviceLogEntryEntity FromDomain(DeviceLogEntry entry) => new()
    {
        Id = entry.Id.ToString("N"),
        CreatedAtUtcTicks = entry.CreatedAtUtc.UtcTicks,
        Severity = (int)entry.Severity,
        Category = entry.Category,
        Message = entry.Message,
        Details = entry.Details,
    };
}
