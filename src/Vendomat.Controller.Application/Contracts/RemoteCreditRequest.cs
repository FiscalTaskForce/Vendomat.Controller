namespace Vendomat.Controller.Application.Contracts;

public sealed class RemoteCreditRequest
{
    public Guid? CommandId { get; set; }
    public decimal Amount { get; set; }
}
