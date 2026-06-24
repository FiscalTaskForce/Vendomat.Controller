namespace Vendomat.Controller.Application.Contracts;

public sealed class PrimingRequest
{
    public Guid? CommandId { get; set; }
    public decimal TargetLiters { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
