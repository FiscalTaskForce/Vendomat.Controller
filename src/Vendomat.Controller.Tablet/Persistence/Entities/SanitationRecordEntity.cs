using SQLite;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Tablet.Persistence.Entities;

[Table("sanitation_records")]
public sealed class SanitationRecordEntity
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string MachineId { get; set; } = Guid.Empty.ToString("N");
    public long StartedAtUtcTicks { get; set; }
    public long DurationTicks { get; set; }
    public int Mode { get; set; }
    public long PulseOnTicks { get; set; }
    public long PulseOffTicks { get; set; }
    public string Notes { get; set; } = string.Empty;

    public SanitationRecord ToDomain() => new()
    {
        Id = Guid.Parse(Id),
        MachineId = Guid.Parse(MachineId),
        StartedAtUtc = new DateTimeOffset(StartedAtUtcTicks, TimeSpan.Zero),
        Duration = TimeSpan.FromTicks(DurationTicks),
        Mode = (SanitationMode)Mode,
        PulseOn = TimeSpan.FromTicks(PulseOnTicks),
        PulseOff = TimeSpan.FromTicks(PulseOffTicks),
        Notes = Notes,
    };

    public static SanitationRecordEntity FromDomain(SanitationRecord record) => new()
    {
        Id = record.Id.ToString("N"),
        MachineId = record.MachineId.ToString("N"),
        StartedAtUtcTicks = record.StartedAtUtc.UtcTicks,
        DurationTicks = record.Duration.Ticks,
        Mode = (int)record.Mode,
        PulseOnTicks = record.PulseOn.Ticks,
        PulseOffTicks = record.PulseOff.Ticks,
        Notes = record.Notes,
    };
}
