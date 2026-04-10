using Vendomat.Controller.Domain.Enums;

namespace Vendomat.Controller.Domain.Models;

public sealed class SaleTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MachineId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public decimal RequestedLiters { get; set; }
    public decimal DispensedLiters { get; set; }
    public decimal PricePerLiter { get; set; }
    public decimal TotalAmount { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public SaleStatus Status { get; set; } = SaleStatus.Pending;
}
