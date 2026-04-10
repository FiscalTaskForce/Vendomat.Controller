namespace Vendomat.Controller.Domain.Models;

public sealed class SanitationDashboardSummary
{
    public int TotalCycles { get; set; }
    public int CyclesLast7Days { get; set; }
    public DateTimeOffset? LastSanitationAtUtc { get; set; }
}
