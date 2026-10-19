using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Pricing;

/// <summary>
/// The one <see cref="IFinalPriceCalculator"/> (round 5, §4.4): <c>FinalPrice = unit price + VAT + every mandatory fee</c>.
/// </summary>
/// <remarks>
/// <para><b>Rounding (spike S21).</b> Integer minor units throughout; each division is rounded once, half away from
/// zero, through <see cref="Money.RoundDiv(long, long)"/>:</para>
/// <list type="bullet">
/// <item>a net price gets its VAT per unit: <c>vat = RoundDiv(net × rate, 10 000)</c>;</item>
/// <item>a gross price is split per unit: <c>net = RoundDiv(gross × 10 000, 10 000 + rate)</c>, <c>vat = gross − net</c>,
/// so net + VAT is exactly the gross the seller typed;</item>
/// <item>a percentage fee is computed per unit on the gross unit price, then multiplied by the quantity, so the price
/// of one unit times N is the price of N units (a listing and a basket never disagree).</item>
/// </list>
/// <para>Fees are VAT-inclusive amounts. A fee with no fixed amount for the price's currency and no percentage
/// contributes nothing (logged nowhere: configuration validation warns at startup instead).</para>
/// <para>Reads <see cref="SparkCommerceOptions"/> through <see cref="IOptionsMonitor{TOptions}"/>, so a fee change in
/// configuration applies to the next calculation; stored prices are never rewritten.</para>
/// </remarks>
internal sealed class FinalPriceCalculator(IOptionsMonitor<SparkCommerceOptions> options) : IFinalPriceCalculator
{
    private const long BasisPoints = 10_000;

    public FinalPrice Calculate(FinalPriceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Quantity < 1)
            throw new ArgumentOutOfRangeException(nameof(request), request.Quantity, "Quantity must be at least 1.");
        if (request.VatRateBasisPoints is < 0 or > 100_00)
            throw new ArgumentOutOfRangeException(nameof(request), request.VatRateBasisPoints, "The VAT rate must be between 0 and 10000 basis points.");

        var channel = options.CurrentValue.GetChannel(request.Channel);
        var currency = request.UnitPrice.Currency;
        var unit = request.UnitPrice.ToMinorUnits();

        long unitNet, unitVat, unitGross;
        if (request.VatRateBasisPoints is not { } rate)
        {
            unitNet = unitGross = unit;
            unitVat = 0;
        }
        else if (request.Basis == PriceBasis.Net)
        {
            unitNet = unit;
            unitVat = Money.RoundDiv(checked(unit * rate), BasisPoints);
            unitGross = checked(unitNet + unitVat);
        }
        else
        {
            unitGross = unit;
            unitNet = Money.RoundDiv(checked(unit * BasisPoints), BasisPoints + rate);
            unitVat = unitGross - unitNet;
        }

        var mandatory = new List<PriceComponent>();
        var optional = new List<PriceComponent>();
        long mandatoryPerUnit = 0, mandatoryPerOrder = 0;
        foreach (var fee in channel.Fees)
        {
            var perUnit = FeePerUnit(fee, unitGross, currency);
            if (perUnit == 0)
                continue;
            var feeTotal = fee.Scope == FeeScope.PerOrder ? perUnit : checked(perUnit * request.Quantity);
            var component = new PriceComponent(fee.Code, fee.Kind, Money.FromMinorUnits(feeTotal, currency), fee.Mandatory,
                fee.LabelKey ?? $"commerce.fee.{fee.Kind}");
            if (fee.Mandatory)
            {
                mandatory.Add(component);
                if (fee.Scope == FeeScope.PerOrder) mandatoryPerOrder += perUnit;
                else mandatoryPerUnit += perUnit;
            }
            else
            {
                optional.Add(component);
            }
        }

        long extra = 0;
        foreach (var charge in request.ExtraMandatoryCharges ?? [])
        {
            if (!string.Equals(charge.Amount.Currency, currency, StringComparison.Ordinal))
                throw new CurrencyMismatchException(currency, charge.Amount.Currency, "final price");
            mandatory.Add(charge with { Mandatory = true });
            extra = checked(extra + charge.Amount.ToMinorUnits());
        }

        var q = request.Quantity;
        var gross = checked(unitGross * q);
        var total = checked(gross + mandatoryPerUnit * q + mandatoryPerOrder + extra);
        // The figure a listing shows: one unit with every per-order and extra charge counted once.
        var unitTotal = checked(unitGross + mandatoryPerUnit + mandatoryPerOrder + extra);

        return new FinalPrice(
            Money.FromMinorUnits(checked(unitNet * q), currency),
            Money.FromMinorUnits(checked(unitVat * q), currency),
            Money.FromMinorUnits(gross, currency),
            mandatory,
            optional,
            Money.FromMinorUnits(total, currency),
            Money.FromMinorUnits(unitTotal, currency));
    }

    private static long FeePerUnit(FeeRuleOptions fee, long unitGross, string currency)
    {
        long amount = 0;
        if (fee.Fixed.TryGetValue(currency, out var fixedAmount) && fixedAmount != 0)
            amount += new Money(fixedAmount, currency).ToMinorUnits();
        if (fee.PercentBasisPoints != 0)
            amount += Money.RoundDiv(checked(unitGross * fee.PercentBasisPoints), BasisPoints);
        return amount;
    }
}
