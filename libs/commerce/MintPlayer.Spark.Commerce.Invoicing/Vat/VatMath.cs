using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Commerce.Invoicing.Vat;

/// <summary>
/// The rounding rules of spike S21 (PRD §13 round 3), on integer minor units. <c>Money.RoundDiv</c> (half away from
/// zero, once per quotient) and <c>Money.Allocate</c> (largest remainder) are the only primitives.
/// <list type="bullet">
/// <item>VAT is rounded <b>once per rate group</b> (EN 16931 BR-CO-17's shape), then split over the group's lines by
/// largest remainder, so lines always sum to the group.</item>
/// <item>The amounts are what the buyer paid (Checkout's line totals); Invoicing never changes an amount, it only splits
/// it into net and VAT. With <see cref="InvoicePricingBasis.Net"/> (the B2B/Peppol default) the group net is chosen so
/// that <c>VAT = round(net × rate)</c> holds exactly whenever an integer net exists (BR-CO-17 exact); otherwise the VAT
/// is extracted from the gross and lies within one minor unit, which the CEN Schematron's BR-CO-17 tolerates (±1).</item>
/// <item>Credit notes mirror the invoice lines with <b>strategy H</b>: per-line cumulative-difference VAT, clamped per
/// line, then nudged a cent at a time toward the group's cumulative target. A full refund cancels the invoice exactly,
/// line by line and group by group (40,000/40,000 orders reconciled in S21).</item>
/// </list>
/// </summary>
public static class VatMath
{
    /// <summary>VAT contained in a gross amount: round(gross × r / (1 + r)).</summary>
    public static long VatFromGross(long gross, int rateBasisPoints)
        => rateBasisPoints == 0 ? 0 : Money.RoundDiv(gross * rateBasisPoints, 10_000 + rateBasisPoints);

    /// <summary>VAT on a net amount: round(net × r).</summary>
    public static long VatFromNet(long net, int rateBasisPoints)
        => rateBasisPoints == 0 ? 0 : Money.RoundDiv(net * rateBasisPoints, 10_000);

    /// <summary>
    /// The VAT of a group whose lines sum to <paramref name="gross"/>: for <see cref="InvoicePricingBasis.Net"/>, the VAT of
    /// a net with <c>net + round(net × r) = gross</c> when one exists (searched around the exact quotient), else the
    /// extracted VAT; for <see cref="InvoicePricingBasis.Gross"/>, the extracted VAT.
    /// </summary>
    public static long GroupVat(long gross, int rateBasisPoints, InvoicePricingBasis basis)
    {
        if (rateBasisPoints == 0 || gross == 0)
            return 0;
        var extracted = VatFromGross(gross, rateBasisPoints);
        if (basis == InvoicePricingBasis.Gross)
            return extracted;
        var center = Money.RoundDiv(gross * 10_000, 10_000 + rateBasisPoints);
        foreach (var net in new[] { center, center - 1, center + 1, center - 2, center + 2 })
        {
            if (net + VatFromNet(net, rateBasisPoints) == gross)
                return gross - net;
        }
        return extracted;
    }

    /// <summary>
    /// Splits paid amounts into net and VAT, per group: <paramref name="groups"/>[i] is line i's group key and
    /// <paramref name="rates"/>[i] its rate. Returns the VAT per line.
    /// </summary>
    public static long[] SplitVat<TKey>(IReadOnlyList<long> gross, IReadOnlyList<TKey> groups, IReadOnlyList<int> rates, InvoicePricingBasis basis)
        where TKey : notnull
    {
        var vat = new long[gross.Count];
        foreach (var group in Enumerable.Range(0, gross.Count).GroupBy(i => groups[i]))
        {
            var ix = group.ToList();
            var sum = ix.Sum(i => gross[i]);
            var groupVat = GroupVat(sum, rates[ix[0]], basis);
            if (sum <= 0 || groupVat == 0)
                continue;
            var parts = Money.Allocate(groupVat, ix.Select(i => Math.Max(0, gross[i])).ToList());
            for (var k = 0; k < ix.Count; k++)
                vat[ix[k]] = parts[k];
        }
        return vat;
    }

    /// <summary>
    /// Strategy H (S21): the VAT of a credit note that refunds <paramref name="refundGross"/>[i] of invoice line i, given
    /// what earlier credit notes already credited (<paramref name="creditedGross"/>, <paramref name="creditedVat"/>).
    /// Invoice lines are (<paramref name="lineGross"/>, <paramref name="lineVat"/>) in group <paramref name="groups"/>[i].
    /// </summary>
    public static long[] CreditVat<TKey>(
        IReadOnlyList<long> lineGross,
        IReadOnlyList<long> lineVat,
        IReadOnlyList<TKey> groups,
        IReadOnlyList<long> refundGross,
        IReadOnlyList<long> creditedGross,
        IReadOnlyList<long> creditedVat)
        where TKey : notnull
    {
        var n = lineGross.Count;
        var vat = new long[n];
        for (var i = 0; i < n; i++)
        {
            var gross = lineGross[i];
            var cum = creditedGross[i] + refundGross[i];
            var cumVat = gross == 0 ? 0 : cum == gross ? lineVat[i] : Money.RoundDiv(lineVat[i] * cum, gross);
            vat[i] = cumVat - creditedVat[i];

            // Clamp to what this line can still credit: 0 <= vat, net; credited never exceeds the invoice line.
            var lineNet = gross - lineVat[i];
            var creditedNet = creditedGross[i] - creditedVat[i];
            var lo = Math.Max(0, refundGross[i] - (lineNet - creditedNet));
            var hi = Math.Min(refundGross[i], lineVat[i] - creditedVat[i]);
            vat[i] = lo > hi ? hi : Math.Clamp(vat[i], lo, hi);
        }

        foreach (var group in Enumerable.Range(0, n).GroupBy(i => groups[i]))
        {
            var ix = group.ToList();
            var gGross = ix.Sum(i => lineGross[i]);
            if (gGross == 0)
                continue;
            var gVat = ix.Sum(i => lineVat[i]);
            var gCum = ix.Sum(i => creditedGross[i] + refundGross[i]);
            var before = ix.Sum(i => creditedVat[i]);
            var target = gCum == gGross ? gVat - before : Money.RoundDiv(gVat * gCum, gGross) - before;
            var delta = target - ix.Sum(i => vat[i]);
            // Only lines this note does not complete may move: L's next cumulative target re-absorbs the nudge.
            var open = ix.Where(i => refundGross[i] > 0 && creditedGross[i] + refundGross[i] < lineGross[i])
                .OrderByDescending(i => refundGross[i]).ThenBy(i => i).ToList();
            var moved = true;
            while (delta != 0 && moved)
            {
                moved = false;
                foreach (var i in open)
                {
                    if (delta == 0)
                        break;
                    var s = Math.Sign(delta);
                    var v = vat[i] + s;
                    var lineNet = lineGross[i] - lineVat[i];
                    var creditedNet = creditedGross[i] - creditedVat[i];
                    if (v < 0 || v > refundGross[i] || creditedVat[i] + v > lineVat[i] || refundGross[i] - v + creditedNet > lineNet)
                        continue;
                    vat[i] = v;
                    delta -= s;
                    moved = true;
                }
            }
        }
        return vat;
    }

    /// <summary>
    /// What each invoice line is credited in total once <paramref name="totalRefunded"/> has been paid back, given what
    /// earlier credit notes already credited: only the difference is spread, first to fully returned lines, then pro
    /// rata (largest remainder) over the open capacity of the lines that take a share. Never above a line's gross, never
    /// below what is credited. Null when nothing is left to credit (a refund an explicit credit note already covered).
    /// </summary>
    public static long[]? CumulativeRefund(IReadOnlyList<long> lineGross, IReadOnlyList<bool> returned, IReadOnlyList<bool> takesShare, IReadOnlyList<long> creditedGross, long totalRefunded)
    {
        var n = lineGross.Count;
        var remaining = totalRefunded - creditedGross.Sum();
        if (remaining <= 0)
            return null;
        var target = creditedGross.ToArray();
        for (var i = 0; i < n && remaining > 0; i++)
        {
            if (!returned[i])
                continue;
            var add = Math.Min(lineGross[i] - target[i], remaining);
            target[i] += add;
            remaining -= add;
        }
        var open = Enumerable.Range(0, n).Where(i => takesShare[i] && lineGross[i] > target[i]).ToList();
        var capacity = open.Select(i => lineGross[i] - target[i]).ToList();
        if (remaining > 0 && capacity.Sum() > 0)
        {
            var parts = Money.Allocate(Math.Min(remaining, capacity.Sum()), capacity);
            for (var k = 0; k < open.Count; k++)
                target[open[k]] += parts[k];
        }
        return target;
    }

    /// <summary>Spreads an order-level amount (a negative discount) over lines pro rata to their gross (§4.6, D19).</summary>
    public static long[] Spread(long amount, IReadOnlyList<long> weights)
    {
        if (amount == 0 || weights.Count == 0 || weights.Sum() <= 0)
            return new long[weights.Count];
        var sign = Math.Sign(amount);
        var parts = Money.Allocate(Math.Abs(amount), weights.Select(w => Math.Max(0, w)).ToList());
        return parts.Select(p => p * sign).ToArray();
    }
}
