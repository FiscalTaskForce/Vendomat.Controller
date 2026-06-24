using Vendomat.Controller.Domain.Enums;

namespace Vendomat.Controller.Domain.Sales;

/// <summary>
/// Pure money/volume calculations for a dispense session. Kept free of hardware and
/// persistence concerns so the rounding rules that move real money can be unit tested.
/// </summary>
public static class SaleMath
{
    /// <summary>Volume (L, 2 decimals, rounded down) a credit buys at the given price.</summary>
    public static decimal LitersFromCredit(decimal creditAmount, decimal pricePerLiter)
    {
        if (creditAmount <= 0 || pricePerLiter <= 0)
        {
            return 0;
        }

        return Math.Floor(creditAmount / pricePerLiter * 100m) / 100m;
    }

    /// <summary>Price (2 decimals) for a requested volume.</summary>
    public static decimal AmountForLiters(decimal liters, decimal pricePerLiter)
    {
        if (liters <= 0 || pricePerLiter <= 0)
        {
            return 0;
        }

        return Math.Round(liters * pricePerLiter, 2);
    }

    /// <summary>Volume actually delivered, clamped to [0, requested] and rounded to 3 decimals.</summary>
    public static decimal ClampDispensed(decimal dispensedLiters, decimal requestedLiters)
    {
        var rounded = Math.Round(Math.Max(0, dispensedLiters), 3);
        return Math.Min(requestedLiters, rounded);
    }

    /// <summary>
    /// Settlement when a dispense is cancelled part-way: how much the customer is charged
    /// for what was delivered and what credit/volume remains available afterwards.
    /// </summary>
    public static CancellationSettlement ComputeCancellation(
        PaymentMethod paymentMethod,
        decimal requestedLiters,
        decimal dispensedLiters,
        decimal currentCreditAmount,
        decimal pricePerLiter)
    {
        var billedLiters = ClampDispensed(dispensedLiters, requestedLiters);
        var billedAmount = AmountForLiters(billedLiters, pricePerLiter);

        if (paymentMethod == PaymentMethod.Cash)
        {
            var remainingCredit = Math.Max(0, Math.Round(currentCreditAmount - billedAmount, 2));
            return new CancellationSettlement(
                billedLiters,
                billedAmount,
                remainingCredit,
                LitersFromCredit(remainingCredit, pricePerLiter),
                remainingCredit);
        }

        var remainingLiters = Math.Max(0, Math.Round(requestedLiters - billedLiters, 2));
        return new CancellationSettlement(
            billedLiters,
            billedAmount,
            0,
            remainingLiters,
            AmountForLiters(remainingLiters, pricePerLiter));
    }
}

/// <param name="BilledLiters">Volume the customer is charged for.</param>
/// <param name="BilledAmount">Money charged for the delivered volume.</param>
/// <param name="RemainingCredit">Cash credit left for a new selection (cash only).</param>
/// <param name="RemainingLiters">Volume still selectable with the leftover.</param>
/// <param name="RemainingTotal">Monetary value of the leftover.</param>
public readonly record struct CancellationSettlement(
    decimal BilledLiters,
    decimal BilledAmount,
    decimal RemainingCredit,
    decimal RemainingLiters,
    decimal RemainingTotal);
