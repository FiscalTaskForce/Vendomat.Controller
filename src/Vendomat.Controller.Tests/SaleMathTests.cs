using Vendomat.Controller.Domain.Enums;
using Vendomat.Controller.Domain.Sales;
using Xunit;

namespace Vendomat.Controller.Tests;

public class SaleMathTests
{
    [Theory]
    [InlineData(10.00, 4.00, 2.50)]   // exact
    [InlineData(10.00, 3.00, 3.33)]   // rounds down (not 3.3333)
    [InlineData(0.99, 1.00, 0.99)]
    [InlineData(0, 4.00, 0)]
    [InlineData(10.00, 0, 0)]         // guard against divide-by-zero price
    [InlineData(-5, 4.00, 0)]
    public void LitersFromCredit_rounds_down_to_two_decimals(decimal credit, decimal price, decimal expected)
    {
        Assert.Equal(expected, SaleMath.LitersFromCredit(credit, price));
    }

    [Fact]
    public void LitersFromCredit_never_overspends_credit()
    {
        const decimal price = 3.00m;
        var liters = SaleMath.LitersFromCredit(10.00m, price);
        Assert.True(liters * price <= 10.00m);
    }

    [Theory]
    [InlineData(2.5, 4.00, 10.00)]
    [InlineData(0.333, 3.00, 1.00)]   // 0.999 -> rounds to 1.00
    [InlineData(0, 4.00, 0)]
    public void AmountForLiters_rounds_to_two_decimals(decimal liters, decimal price, decimal expected)
    {
        Assert.Equal(expected, SaleMath.AmountForLiters(liters, price));
    }

    [Theory]
    [InlineData(1.5, 2.0, 1.5)]       // within request
    [InlineData(2.5, 2.0, 2.0)]       // clamped to request
    [InlineData(-1, 2.0, 0)]          // never negative
    public void ClampDispensed_stays_within_bounds(decimal dispensed, decimal requested, decimal expected)
    {
        Assert.Equal(expected, SaleMath.ClampDispensed(dispensed, requested));
    }

    [Fact]
    public void ComputeCancellation_cash_refunds_unused_credit()
    {
        // Paid 10 RON cash, price 4/L (=> 2.5 L selectable), stopped after 1 L.
        var settlement = SaleMath.ComputeCancellation(
            PaymentMethod.Cash,
            requestedLiters: 2.5m,
            dispensedLiters: 1.0m,
            currentCreditAmount: 10.00m,
            pricePerLiter: 4.00m);

        Assert.Equal(1.0m, settlement.BilledLiters);
        Assert.Equal(4.00m, settlement.BilledAmount);
        Assert.Equal(6.00m, settlement.RemainingCredit);
        Assert.Equal(1.5m, settlement.RemainingLiters);
        Assert.Equal(6.00m, settlement.RemainingTotal);
    }

    [Fact]
    public void ComputeCancellation_cash_never_returns_negative_credit()
    {
        var settlement = SaleMath.ComputeCancellation(
            PaymentMethod.Cash,
            requestedLiters: 2.5m,
            dispensedLiters: 2.5m,
            currentCreditAmount: 10.00m,
            pricePerLiter: 4.00m);

        Assert.Equal(0m, settlement.RemainingCredit);
        Assert.Equal(0m, settlement.RemainingLiters);
    }

    [Fact]
    public void ComputeCancellation_card_bills_only_delivered_volume()
    {
        var settlement = SaleMath.ComputeCancellation(
            PaymentMethod.Card,
            requestedLiters: 2.0m,
            dispensedLiters: 0.5m,
            currentCreditAmount: 0m,
            pricePerLiter: 4.00m);

        Assert.Equal(0.5m, settlement.BilledLiters);
        Assert.Equal(2.00m, settlement.BilledAmount);
        Assert.Equal(0m, settlement.RemainingCredit);          // card never accrues credit
        Assert.Equal(1.5m, settlement.RemainingLiters);
        Assert.Equal(6.00m, settlement.RemainingTotal);
    }

    [Fact]
    public void ComputeCancellation_clamps_overrun_dispense()
    {
        // Sensor over-reports beyond the requested volume; customer must not be over-charged.
        var settlement = SaleMath.ComputeCancellation(
            PaymentMethod.Card,
            requestedLiters: 1.0m,
            dispensedLiters: 1.4m,
            currentCreditAmount: 0m,
            pricePerLiter: 5.00m);

        Assert.Equal(1.0m, settlement.BilledLiters);
        Assert.Equal(5.00m, settlement.BilledAmount);
        Assert.Equal(0m, settlement.RemainingLiters);
    }
}
