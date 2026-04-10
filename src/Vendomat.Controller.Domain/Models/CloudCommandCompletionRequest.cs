namespace Vendomat.Controller.Domain.Models;

public sealed class CloudCommandCompletionRequest
{
    public Guid MachineId { get; set; }
    public string MachineToken { get; set; } = string.Empty;
    public Guid CommandId { get; set; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}
