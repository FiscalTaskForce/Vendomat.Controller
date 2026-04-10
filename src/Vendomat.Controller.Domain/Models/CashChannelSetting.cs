namespace Vendomat.Controller.Domain.Models;

public sealed class CashChannelSetting
{
    public int Channel { get; set; }
    public string Label { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public bool IsEnabled { get; set; }
}
