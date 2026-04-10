namespace Vendomat.Controller.Domain.Models;

public sealed class CloudMachineSyncResult
{
    public List<CloudCommandEnvelope> PendingCommands { get; set; } = [];
    public DateTimeOffset ServerTimeUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool HasActiveWatcher { get; set; }
}
