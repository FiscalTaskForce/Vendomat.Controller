using Vendomat.Controller.Domain.Enums;

namespace Vendomat.Controller.Domain.Models;

public sealed class DispenseSessionState
{
    public decimal RequestedLiters { get; set; }
    public decimal DispensedLiters { get; set; }
    public decimal CurrentCreditAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public bool IsCardSelectionBlocked { get; set; }
    public MachineActivityState ActivityState { get; set; } = MachineActivityState.Ready;
    public PaymentMethod? ActivePaymentMethod { get; set; }
}
