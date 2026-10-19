using System.Diagnostics;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// Spike S21 (commerce PRD §7), kept as a seeded property test: 10,000 generated orders × 4 VAT
/// configurations, priced, discounted, invoiced and refunded through <see cref="Money.RoundDiv(long, long)"/>
/// and <see cref="Money.Allocate(long, IReadOnlyList{long})"/> alone, and every total, VAT base and
/// credit note must reconcile to the cent. Runs in about a second.
/// </summary>
/// <remarks>
/// <para>
/// The engine below is the <b>reference</b> for the invoicing milestone (M10), not production code: it
/// states the rules S21 settled on, so a change to either primitive is checked against them here.
/// </para>
/// <list type="table">
/// <item><term>R-a</term><description>Integer minor units; each quotient rounded half away from zero, once.</description></item>
/// <item><term>R-b</term><description>Line = unit × qty − line discount; a percentage discount is rounded once, on the line.</description></item>
/// <item><term>R-c</term><description>Order discount: a percentage rounded once on the discountable base, a fixed amount capped at it,
/// allocated pro rata over the discountable lines by largest remainder. Fees are not discountable.</description></item>
/// <item><term>R-d</term><description>Pricing Gross (B2C) or Net (B2B); VAT rounded per rate group (split by largest remainder) or per line.</description></item>
/// <item><term>R-e</term><description>Refund by units: cumulative = round(lineGross × units / qty), minus what is already refunded.</description></item>
/// <item><term>R-f</term><description>Refund by amount: allocated over what each line can still refund, largest remainder.</description></item>
/// <item><term>R-g/R-h</term><description>Credit notes mirror invoice lines with cumulative-difference VAT, clamped, then steered
/// towards the rate group's cumulative target (strategy H, the recommended one).</description></item>
/// </list>
/// <para>
/// Strategy G (re-allocating a group's cumulative VAT by largest remainder at every refund) was measured to
/// fail ~0.5% of configurations, because largest remainder is not monotone; it is deliberately absent.
/// </para>
/// </remarks>
public class MoneyRoundingPropertyTests
{
    private enum Pricing { Gross, Net }
    private enum VatCalc { PerRateGroup, PerLine }

    private sealed record LineSpec(int RatePct, long Unit, int Qty, char DiscKind, long DiscValue, bool Discountable);
    private sealed record OrderSpec(List<LineSpec> Lines, char OrderDiscKind, long OrderDiscValue);
    private sealed record RefundStep(char Kind, double A, double B); // U = units of a line, A = amount share, R = remainder

    private sealed class InvLine
    {
        public int Rate;
        public long Base0, OrderAlloc, Amount, Net, Vat, Gross;
    }

    private sealed class Invoice
    {
        public List<InvLine> Lines = [];
        public Dictionary<int, (long Base, long Vat)> Groups = [];
        public long Total;
    }

    private sealed class CreditNote
    {
        public long[] Gross = [], Net = [], Vat = [];
        public Dictionary<int, (long Base, long Vat)> Groups = [];
    }

    private sealed class Stats
    {
        public int Configs, Passed, CreditNotes, FullyRefunded, NegativeCreditComponents, InvoiceBrCo17OffNetPerGroup;
        public readonly List<string> Failures = [];
    }

    private static long VatFromNet(long net, int pct) => Money.RoundDiv(net * pct, 100);
    private static long VatFromGross(long gross, int pct) => Money.RoundDiv(gross * pct, 100 + pct);

    [Fact]
    public void Ten_thousand_orders_reconcile_to_the_cent_in_every_configuration()
    {
        var stats = Run(seed: 521, orders: 10_000);

        stats.Failures.Should().BeEmpty();
        stats.Passed.Should().Be(40_000);
        stats.Configs.Should().Be(40_000);
        // The run must actually exercise refunds, or "no failures" proves little.
        stats.CreditNotes.Should().BeGreaterThan(50_000);
        stats.FullyRefunded.Should().BeGreaterThan(5_000);
        // Strategy H never produces a negative credit-note line (the bug its clamp fixed in S21).
        stats.NegativeCreditComponents.Should().Be(0);
        // Net pricing with VAT rounded per rate group always meets EN 16931 BR-CO-17 exactly: the B2B default.
        stats.InvoiceBrCo17OffNetPerGroup.Should().Be(0);
    }

    [Fact]
    public void A_second_seed_reconciles_too()
    {
        var stats = Run(seed: 7, orders: 2_000);

        stats.Failures.Should().BeEmpty();
        stats.NegativeCreditComponents.Should().Be(0);
    }

    private static Stats Run(int seed, int orders)
    {
        var stats = new Stats();
        var rng = new Random(seed);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < orders; i++)
        {
            var order = GenerateOrder(rng);
            var plan = GenerateRefunds(rng);
            foreach (var pricing in new[] { Pricing.Gross, Pricing.Net })
            foreach (var vatCalc in new[] { VatCalc.PerRateGroup, VatCalc.PerLine })
            {
                stats.Configs++;
                try
                {
                    Check(order, plan, pricing, vatCalc, stats);
                    stats.Passed++;
                }
                catch (Exception ex) when (stats.Failures.Count < 10)
                {
                    stats.Failures.Add($"seed {seed} order {i} {pricing}/{vatCalc}: {ex.Message}");
                }
                catch (Exception)
                {
                    // Already reporting ten; the count is what matters past that.
                }
            }
        }
        Debug.WriteLine($"S21 seed {seed}: {orders} orders in {watch.ElapsedMilliseconds} ms");
        return stats;
    }

    // --- invoice --------------------------------------------------------------------------------

    private static Invoice Build(OrderSpec o, Pricing pricing, VatCalc calc)
    {
        var inv = new Invoice();
        foreach (var l in o.Lines)
        {
            var ext = l.Unit * l.Qty;
            var disc = l.DiscKind switch
            {
                'P' => Money.RoundDiv(ext * l.DiscValue, 10_000), // basis points
                'F' => Math.Min(l.DiscValue, ext),
                _ => 0,
            };
            inv.Lines.Add(new InvLine { Rate = l.RatePct, Base0 = ext - disc });
        }

        var idx = Enumerable.Range(0, o.Lines.Count).Where(i => o.Lines[i].Discountable).ToList();
        var discBase = idx.Sum(i => inv.Lines[i].Base0);
        var orderDisc = o.OrderDiscKind switch
        {
            'P' => Money.RoundDiv(discBase * o.OrderDiscValue, 10_000),
            'F' => Math.Min(o.OrderDiscValue, discBase),
            _ => 0,
        };
        if (orderDisc > 0)
        {
            var alloc = Money.Allocate(orderDisc, idx.Select(i => inv.Lines[i].Base0).ToList());
            for (var k = 0; k < idx.Count; k++)
            {
                var ln = inv.Lines[idx[k]];
                ln.OrderAlloc = alloc[k];
                var exact = (decimal)orderDisc * ln.Base0 / discBase;
                if (Math.Abs(alloc[k] - exact) >= 1m) throw new Exception($"I1 alloc {alloc[k]} vs exact {exact}");
            }
            if (alloc.Sum() != orderDisc) throw new Exception("I1 allocation does not sum");
        }
        foreach (var ln in inv.Lines)
        {
            ln.Amount = ln.Base0 - ln.OrderAlloc;
            if (ln.Amount < 0) throw new Exception("I1 negative line");
        }

        foreach (var g in inv.Lines.GroupBy(l => l.Rate))
        {
            var ls = g.ToList();
            var p = g.Key;
            if (calc == VatCalc.PerLine)
            {
                foreach (var ln in ls)
                {
                    if (pricing == Pricing.Gross) { ln.Gross = ln.Amount; ln.Vat = VatFromGross(ln.Gross, p); ln.Net = ln.Gross - ln.Vat; }
                    else { ln.Net = ln.Amount; ln.Vat = VatFromNet(ln.Net, p); ln.Gross = ln.Net + ln.Vat; }
                }
            }
            else
            {
                // VAT rounded once per rate group (the BR-CO-17 shape), then split over the lines.
                var sum = ls.Sum(l => l.Amount);
                var groupVat = pricing == Pricing.Gross ? VatFromGross(sum, p) : VatFromNet(sum, p);
                var split = sum == 0 ? new long[ls.Count] : Money.Allocate(groupVat, ls.Select(l => l.Amount).ToList());
                for (var k = 0; k < ls.Count; k++)
                {
                    var ln = ls[k];
                    ln.Vat = split[k];
                    if (pricing == Pricing.Gross) { ln.Gross = ln.Amount; ln.Net = ln.Gross - ln.Vat; }
                    else { ln.Net = ln.Amount; ln.Gross = ln.Net + ln.Vat; }
                }
            }
            inv.Groups[p] = (ls.Sum(l => l.Net), ls.Sum(l => l.Vat));
        }
        inv.Total = inv.Lines.Sum(l => l.Gross);
        return inv;
    }

    // --- credit notes, strategy H ------------------------------------------------------------------

    private static CreditNote Issue(Invoice inv, long[] refundGross, long[] cumBefore, long[] vatBefore)
    {
        var n = inv.Lines.Count;
        var cn = new CreditNote { Gross = refundGross, Net = new long[n], Vat = new long[n] };

        // R-g: cumulative-difference VAT per line, so a full refund cancels the invoice exactly.
        for (var i = 0; i < n; i++)
        {
            var ln = inv.Lines[i];
            var cum = cumBefore[i] + refundGross[i];
            var cumVat = ln.Gross == 0 ? 0 : cum == ln.Gross ? ln.Vat : Money.RoundDiv(ln.Vat * cum, ln.Gross);
            cn.Vat[i] = cumVat - vatBefore[i];
            cn.Net[i] = refundGross[i] - cn.Vat[i];
        }

        // R-h, step 1: clamp to what the line can still credit.
        for (var i = 0; i < n; i++)
        {
            var ln = inv.Lines[i];
            var r = refundGross[i];
            var lo = Math.Max(0, r - (ln.Net - (cumBefore[i] - vatBefore[i])));
            var hi = Math.Min(r, ln.Vat - vatBefore[i]);
            cn.Vat[i] = Math.Clamp(cn.Vat[i], lo, hi);
            cn.Net[i] = r - cn.Vat[i];
        }

        // R-h, step 2: steer each group's VAT towards its cumulative target, one cent at a time, on lines
        // this note does not complete.
        foreach (var g in Enumerable.Range(0, n).GroupBy(i => inv.Lines[i].Rate))
        {
            var ix = g.ToList();
            var gGross = ix.Sum(i => inv.Lines[i].Gross);
            var gVat = ix.Sum(i => inv.Lines[i].Vat);
            var gCum = ix.Sum(i => cumBefore[i] + refundGross[i]);
            if (gGross != 0)
            {
                var target = gCum == gGross
                    ? gVat - ix.Sum(i => vatBefore[i])
                    : Money.RoundDiv(gVat * gCum, gGross) - ix.Sum(i => vatBefore[i]);
                var delta = target - ix.Sum(i => cn.Vat[i]);
                var open = ix.Where(i => refundGross[i] > 0 && cumBefore[i] + refundGross[i] < inv.Lines[i].Gross)
                    .OrderByDescending(i => refundGross[i]).ThenBy(i => i).ToList();
                var moved = true;
                while (delta != 0 && moved)
                {
                    moved = false;
                    foreach (var i in open)
                    {
                        if (delta == 0) break;
                        var s = Math.Sign(delta);
                        var v = cn.Vat[i] + s;
                        if (v < 0 || v > refundGross[i] || vatBefore[i] + v > inv.Lines[i].Vat
                            || refundGross[i] - v + (cumBefore[i] - vatBefore[i]) > inv.Lines[i].Net) continue;
                        cn.Vat[i] = v;
                        cn.Net[i] = refundGross[i] - v;
                        delta -= s;
                        moved = true;
                    }
                }
            }
            cn.Groups[g.Key] = (ix.Sum(i => cn.Net[i]), ix.Sum(i => cn.Vat[i]));
        }
        return cn;
    }

    // --- the checks --------------------------------------------------------------------------------

    private static void Check(OrderSpec o, List<RefundStep> plan, Pricing pricing, VatCalc calc, Stats st)
    {
        var inv = Build(o, pricing, calc);

        // I2-I4
        foreach (var l in inv.Lines)
            if (l.Net + l.Vat != l.Gross || l.Net < 0 || l.Vat < 0) throw new Exception("I2 line");
        if (inv.Groups.Values.Sum(g => g.Base + g.Vat) != inv.Total) throw new Exception("I4 base+vat != total");
        if (pricing == Pricing.Gross && inv.Total != inv.Lines.Sum(l => l.Amount)) throw new Exception("I4 gross price changed");
        if (pricing == Pricing.Net && calc == VatCalc.PerRateGroup)
            foreach (var (p, g) in inv.Groups)
                if (g.Vat != VatFromNet(g.Base, p)) st.InvoiceBrCo17OffNetPerGroup++;

        var n = inv.Lines.Count;
        var cum = new long[n];
        var cumVat = new long[n];
        var cumNet = new long[n];
        var unitsDone = new int[n];
        long refundedTotal = 0;
        foreach (var step in plan)
        {
            var remaining = Enumerable.Range(0, n).Select(i => inv.Lines[i].Gross - cum[i]).ToArray();
            var remTotal = remaining.Sum();
            if (remTotal == 0) break;
            var r = new long[n];
            if (step.Kind == 'U')
            {
                var cand = Enumerable.Range(0, n).Where(i => unitsDone[i] < o.Lines[i].Qty && remaining[i] > 0).ToList();
                if (cand.Count == 0) continue;
                var i = cand[(int)(step.A * cand.Count)];
                var qty = o.Lines[i].Qty;
                var k = 1 + (int)(step.B * (qty - unitsDone[i]));
                unitsDone[i] += k;
                var target = unitsDone[i] == qty ? inv.Lines[i].Gross : Money.RoundDiv(inv.Lines[i].Gross * unitsDone[i], qty);
                r[i] = target - cum[i];
                var exact = (decimal)inv.Lines[i].Gross * k / qty;
                if (Math.Abs(r[i] - exact) >= 1m) throw new Exception($"R4 unit refund {r[i]} vs {exact}");
            }
            else
            {
                var amount = step.Kind == 'R' ? remTotal : Math.Max(1, (long)(step.A * remTotal));
                r = Money.Allocate(amount, remaining);
                for (var i = 0; i < n; i++)
                {
                    if (remaining[i] == 0) continue;
                    var exact = (decimal)amount * remaining[i] / remTotal;
                    if (Math.Abs(r[i] - exact) >= 1m) throw new Exception("R5 amount allocation");
                    unitsDone[i] = o.Lines[i].Qty;
                }
            }

            var cn = Issue(inv, r, cum, cumVat);
            for (var i = 0; i < n; i++)
            {
                if (r[i] < 0 || cum[i] + r[i] > inv.Lines[i].Gross) throw new Exception("R1 line over-refunded");
                if (cn.Net[i] + cn.Vat[i] != cn.Gross[i]) throw new Exception("R2 credit line");
                if (cn.Vat[i] < 0 || cn.Net[i] < 0) st.NegativeCreditComponents++;
                cum[i] += r[i];
                cumVat[i] += cn.Vat[i];
                cumNet[i] += cn.Net[i];
                if (cumVat[i] > inv.Lines[i].Vat || cumNet[i] > inv.Lines[i].Net) throw new Exception("R3 cumulative credit exceeds invoice line");
            }
            if (cn.Groups.Values.Sum(g => g.Base + g.Vat) != r.Sum()) throw new Exception("R2 credit total");
            refundedTotal += r.Sum();
            st.CreditNotes++;
        }

        if (refundedTotal > inv.Total) throw new Exception("R3 refunded more than paid");
        if (refundedTotal == inv.Total)
        {
            st.FullyRefunded++;
            for (var i = 0; i < n; i++)
                if (cum[i] != inv.Lines[i].Gross || cumVat[i] != inv.Lines[i].Vat || cumNet[i] != inv.Lines[i].Net)
                    throw new Exception("R3 full refund does not mirror invoice line");
        }
    }

    // --- the generator (identical draws to S21, so seed 521 reproduces its run) ---------------------

    private static readonly int[] Rates = [21, 12, 6, 0];

    private static OrderSpec GenerateOrder(Random r)
    {
        var lines = new List<LineSpec>();
        var nLines = r.Next(1, 9);
        for (var i = 0; i < nLines; i++)
        {
            long unit = r.Next(10) switch
            {
                0 => r.Next(1, 100),             // cents
                < 5 => r.Next(1, 100) * 100 - 1, // x.99
                _ => r.Next(1, 50_001),          // up to 500.00
            };
            var qty = r.Next(10) < 6 ? 1 + r.Next(3) : r.Next(1, 21);
            var dk = r.Next(20) switch { < 12 => 'N', < 17 => 'P', _ => 'F' };
            long dv = dk == 'P' ? r.Next(1, 5001) : dk == 'F' ? r.Next(1, (int)Math.Min(int.MaxValue, unit * qty + 1)) : 0;
            lines.Add(new LineSpec(Rates[r.Next(4)], unit, qty, dk, dv, true));
        }
        if (r.Next(10) < 3) lines.Add(new LineSpec(21, r.Next(50, 500), 1, 'N', 0, false)); // service fee, not discountable
        var ok = r.Next(10) switch { < 5 => 'N', < 8 => 'P', _ => 'F' };
        long ov = ok switch
        {
            'P' => r.Next(1, 10_001),
            'F' => r.Next(5) == 0 ? long.MaxValue : r.Next(1, 20_001), // MaxValue = capped at the whole base
            _ => 0,
        };
        return new OrderSpec(lines, ok, ov);
    }

    private static List<RefundStep> GenerateRefunds(Random r)
    {
        var steps = new List<RefundStep>();
        var n = r.Next(5);
        for (var i = 0; i < n; i++) steps.Add(new RefundStep(r.Next(2) == 0 ? 'U' : 'A', r.NextDouble(), r.NextDouble()));
        if (r.Next(4) == 0) steps.Add(new RefundStep('R', 0, 0));
        return steps;
    }
}
