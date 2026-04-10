namespace Vendomat.Controller.Domain.Models;

public sealed class MachineDashboardSnapshot
{
    public MachineStatusSnapshot Status { get; set; } = new();
    public SalesDashboardSummary Sales { get; set; } = new();
    public SanitationDashboardSummary Sanitation { get; set; } = new();
    public List<SaleTransaction> RecentSales { get; set; } = [];
    public List<SanitationRecord> RecentSanitations { get; set; } = [];
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
