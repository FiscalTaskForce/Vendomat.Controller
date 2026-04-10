using Vendomat.Controller.Domain.Enums;

namespace Vendomat.Controller.Domain.Models;

public sealed class SanitationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MachineId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public TimeSpan Duration { get; set; }
    public SanitationMode Mode { get; set; }
    public TimeSpan PulseOn { get; set; }
    public TimeSpan PulseOff { get; set; }
    public string Notes { get; set; } = string.Empty;
}
