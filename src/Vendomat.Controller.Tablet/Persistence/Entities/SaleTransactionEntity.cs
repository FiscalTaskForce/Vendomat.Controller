using SQLite;
using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Tablet.Persistence.Entities;

[Table("sale_transactions")]
public sealed class SaleTransactionEntity
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string MachineId { get; set; } = Guid.Empty.ToString("N");
    public long StartedAtUtcTicks { get; set; }
    public long CompletedAtUtcTicks { get; set; }
    public decimal RequestedLiters { get; set; }
    public decimal DispensedLiters { get; set; }
    public decimal PricePerLiter { get; set; }
    public decimal TotalAmount { get; set; }
    public int PaymentMethod { get; set; }
    public int Status { get; set; }

    public SaleTransaction ToDomain() => new()
    {
        Id = Guid.Parse(Id),
        MachineId = Guid.Parse(MachineId),
        StartedAtUtc = new DateTimeOffset(StartedAtUtcTicks, TimeSpan.Zero),
        CompletedAtUtc = CompletedAtUtcTicks <= 0 ? null : new DateTimeOffset(CompletedAtUtcTicks, TimeSpan.Zero),
        RequestedLiters = RequestedLiters,
        DispensedLiters = DispensedLiters,
        PricePerLiter = PricePerLiter,
        TotalAmount = TotalAmount,
        PaymentMethod = (PaymentMethod)PaymentMethod,
        Status = (SaleStatus)Status,
    };

    public static SaleTransactionEntity FromDomain(SaleTransaction transaction) => new()
    {
        Id = transaction.Id.ToString("N"),
        MachineId = transaction.MachineId.ToString("N"),
        StartedAtUtcTicks = transaction.StartedAtUtc.UtcTicks,
        CompletedAtUtcTicks = transaction.CompletedAtUtc?.UtcTicks ?? 0,
        RequestedLiters = transaction.RequestedLiters,
        DispensedLiters = transaction.DispensedLiters,
        PricePerLiter = transaction.PricePerLiter,
        TotalAmount = transaction.TotalAmount,
        PaymentMethod = (int)transaction.PaymentMethod,
        Status = (int)transaction.Status,
    };
}
