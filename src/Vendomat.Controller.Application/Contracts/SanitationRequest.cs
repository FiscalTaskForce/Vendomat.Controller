using Vendomat.Controller.Domain.Enums;

namespace Vendomat.Controller.Application.Contracts;

public sealed class SanitationRequest
{
    public SanitationMode Mode { get; set; }
    public TimeSpan Duration { get; set; }
    public TimeSpan PulseOn { get; set; }
    public TimeSpan PulseOff { get; set; }
}
