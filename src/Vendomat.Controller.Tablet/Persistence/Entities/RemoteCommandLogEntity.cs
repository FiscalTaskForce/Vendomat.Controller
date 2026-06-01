using SQLite;

namespace Vendomat.Controller.Tablet.Persistence.Entities;

[Table("remote_command_logs")]
public sealed class RemoteCommandLogEntity
{
    [PrimaryKey]
    public string CommandId { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string ResultMessage { get; set; } = string.Empty;
    public long CreatedAtUtcTicks { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
}
