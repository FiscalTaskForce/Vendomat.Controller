using Vendomat.Controller.Domain.Enums;

namespace Vendomat.Controller.Domain.Models;

public sealed class CloudSanitationCommand
{
    public SanitationMode Mode { get; set; }
    public TimeSpan Duration { get; set; }
    public TimeSpan PulseOn { get; set; }
    public TimeSpan PulseOff { get; set; }
}
