using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Checkout.Services;

/// <summary>
/// The net and VAT of each priced line, from the amount the buyer actually pays for the goods (S21, PRD §13 round 3):
/// the line total minus its mandatory fees, i.e. after every pricing step's adjustment (a promotion, a sale price).
/// </summary>
/// <remarks>
/// <para>Before this ran, a line kept the calculator's split of the undiscounted price, so a discounted line carried a
/// VAT base that did not match its total and the order's VAT summed to more than was charged.</para>
/// <para>Rules (the same shape Invoicing's <c>VatMath</c> uses, so Checkout's figures are the inputs strategy H mirrors):</para>
/// <list type="bullet">
/// <item>An unadjusted line keeps the calculator's split (exact for the price the seller typed).</item>
/// <item><see cref="CheckoutVatRounding.PerLine"/>: an adjusted line's VAT is extracted once from its goods amount,
/// <c>RoundDiv(gross × r, 10 000 + r)</c>; for a <see cref="PriceBasis.Net"/> item the net with
/// <c>net + RoundDiv(net × r, 10 000) = gross</c> is preferred when one exists (BR-CO-17 exact).</item>
/// <item><see cref="CheckoutVatRounding.PerGroup"/>: VAT is rounded once per (rate, basis) group and split over the
/// group's lines by largest remainder (<c>Money.Allocate</c>), so the lines always sum to the group.</item>
/// <item>A line without a rate has no VAT: net = goods amount.</item>
/// </list>
/// </remarks>
internal static class CheckoutLineVat
{
    private const long BasisPoints = 10_000;

    public static void Apply(IReadOnlyList<PricedLine> lines, CheckoutVatRounding rounding)
    {
        if (rounding == CheckoutVatRounding.PerGroup)
        {
            foreach (var line in lines.Where(l => l.Item.VatRateBasisPoints is null))
                SetNoRate(line);
            foreach (var group in lines.Where(l => l.Item.VatRateBasisPoints is not null).GroupBy(l => (l.Item.VatRateBasisPoints!.Value, l.Item.PriceBasis)))
            {
                var members = group.ToList();
                var gross = members.Select(Goods).ToList();
                var sum = gross.Sum();
                var groupVat = sum <= 0 ? 0 : Vat(sum, group.Key.Value, group.Key.PriceBasis);
                var parts = groupVat == 0 ? new long[members.Count] : Money.Allocate(groupVat, gross.Select(g => Math.Max(0, g)).ToList());
                for (var k = 0; k < members.Count; k++)
                    Set(members[k], gross[k] - parts[k], parts[k]);
            }
            return;
        }

        foreach (var line in lines)
        {
            if (line.Item.VatRateBasisPoints is not { } rate)
            {
                SetNoRate(line);
                continue;
            }
            if (line.Adjustments.All(a => a.Amount.IsZero))
            {
                line.Net = line.Price.Net;
                line.Vat = line.Price.Vat;
                continue;
            }
            var goods = Goods(line);
            var vat = Vat(goods, rate, line.Item.PriceBasis);
            Set(line, goods - vat, vat);
        }
    }

    /// <summary>What the buyer pays for the goods of <paramref name="line"/>: its total minus its mandatory fees.</summary>
    public static long Goods(PricedLine line)
        => line.Total.ToMinorUnits() - line.Price.MandatoryFees.Sum(f => f.Amount.ToMinorUnits());

    /// <summary>The VAT contained in <paramref name="gross"/>, rounded once.</summary>
    public static long Vat(long gross, int rate, PriceBasis basis)
    {
        if (rate == 0 || gross == 0)
            return 0;
        var extracted = Money.RoundDiv(checked(gross * rate), BasisPoints + rate);
        if (basis != PriceBasis.Net)
            return extracted;
        var center = gross - extracted;
        foreach (var net in new[] { center, center - 1, center + 1, center - 2, center + 2 })
        {
            if (net + Money.RoundDiv(checked(net * rate), BasisPoints) == gross)
                return gross - net;
        }
        return extracted;
    }

    private static void SetNoRate(PricedLine line)
    {
        var goods = Goods(line);
        Set(line, goods, 0);
    }

    private static void Set(PricedLine line, long net, long vat)
    {
        var currency = line.Price.Total.Currency;
        line.Net = Money.FromMinorUnits(net, currency);
        line.Vat = Money.FromMinorUnits(vat, currency);
    }
}
