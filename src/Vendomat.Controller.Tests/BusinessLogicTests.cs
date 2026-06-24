using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Domain.Sales;
using Xunit;

namespace Vendomat.Controller.Tests;

/// <summary>
/// Unit tests for stock/inventory business rules and the pricing maths that drive revenue,
/// exercised purely against the domain layer (no hardware, no database).
/// </summary>
public class BusinessLogicTests
{
    [Theory]
    [InlineData(100, 200, 0.5)]
    [InlineData(0, 200, 0)]
    [InlineData(200, 200, 1)]
    public void StockFillPercent_is_the_ratio_of_stock_to_capacity(decimal stock, decimal capacity, decimal expected)
    {
        var settings = new MachineSettings { CurrentStockLiters = stock, TankCapacityLiters = capacity };
        Assert.Equal(expected, settings.StockFillPercent);
    }

    [Fact]
    public void StockFillPercent_clamps_to_the_unit_interval()
    {
        var overFull = new MachineSettings { CurrentStockLiters = 250m, TankCapacityLiters = 200m };
        var negative = new MachineSettings { CurrentStockLiters = -10m, TankCapacityLiters = 200m };

        Assert.Equal(1m, overFull.StockFillPercent);
        Assert.Equal(0m, negative.StockFillPercent);
    }

    [Fact]
    public void StockFillPercent_guards_against_zero_capacity()
    {
        var settings = new MachineSettings { CurrentStockLiters = 10m, TankCapacityLiters = 0m };
        Assert.Equal(0m, settings.StockFillPercent);
    }

    [Theory]
    [InlineData(15, 20, true)]   // at/under threshold -> refill alert
    [InlineData(20, 20, true)]
    [InlineData(25, 20, false)]  // above threshold -> healthy
    public void Low_stock_alert_triggers_at_or_below_the_threshold(decimal stock, decimal threshold, bool shouldAlert)
    {
        var settings = new MachineSettings { CurrentStockLiters = stock, LowStockThresholdLiters = threshold };
        var isLow = settings.CurrentStockLiters <= settings.LowStockThresholdLiters;
        Assert.Equal(shouldAlert, isLow);
    }

    [Fact]
    public void Remaining_stock_value_uses_the_sale_pricing_rules()
    {
        var settings = new MachineSettings { CurrentStockLiters = 30m, PricePerLiter = 9.5m };
        var value = SaleMath.AmountForLiters(settings.CurrentStockLiters, settings.PricePerLiter);
        Assert.Equal(285.00m, value);
    }

    [Fact]
    public void Sale_revenue_matches_the_dispensed_volume()
    {
        // A completed sale of 2.4 L at 7.5 RON/L should bill exactly 18.00 RON.
        var billed = SaleMath.AmountForLiters(2.4m, 7.5m);
        Assert.Equal(18.00m, billed);
    }
}
