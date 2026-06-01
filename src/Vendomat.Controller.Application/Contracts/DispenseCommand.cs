using Vendomat.Controller.Domain.Enums;

namespace Vendomat.Controller.Application.Contracts;

public sealed class DispenseCommand
{
    public Guid? CommandId { get; set; }
    public decimal RequestedLiters { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public decimal CreditAmount { get; set; }
}
