namespace Vendomat.Controller.Domain.Models;

public sealed class SalesDashboardSummary
{
    public decimal TodayRevenue { get; set; }
    public decimal TodayLiters { get; set; }
    public int TodayCompletedSales { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal TotalLiters { get; set; }
    public int TotalCompletedSales { get; set; }
    public DateTimeOffset? LastSaleAtUtc { get; set; }
}
