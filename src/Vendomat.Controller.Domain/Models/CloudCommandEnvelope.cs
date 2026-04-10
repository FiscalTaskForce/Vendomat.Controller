namespace Vendomat.Controller.Domain.Models;

public sealed class CloudCommandEnvelope
{
    public Guid CommandId { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public MachineSettings? Settings { get; set; }
    public CloudSanitationCommand? SanitationCommand { get; set; }
    public CloudCreditCommand? CreditCommand { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
