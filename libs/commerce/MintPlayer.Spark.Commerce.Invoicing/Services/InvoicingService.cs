using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using MintPlayer.Spark.Commerce.Invoicing.Vat;
using MintPlayer.Spark.Commerce.Payments.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Invoicing.Services;

/// <summary>The buyer as the VAT rules see it.</summary>
internal sealed record InvoiceBuyer(
    InvoiceParty Party,
    bool Business,
    bool InvoiceRequested,
    string? ShipToCountry,
    bool ShipToOutsideVatTerritory,
    bool BuyerOutsideVatTerritory,
    string Language);

/// <summary>A line before VAT: what was paid for it (gross, in minor units) and what the rules need.</summary>
internal sealed record InvoiceDraftLine(
    string SourceLineId,
    InvoiceLineSource Source,
    string Title,
    int Quantity,
    long Gross,
    string? Category,
    bool IsGoods,
    string? EventCountry,
    string? Channel,
    DateOnly? SupplyDate,
    int? CheckoutRate);

/// <summary>The implementation of <see cref="ISparkInvoicing"/>.</summary>
internal sealed class InvoicingService(
    IDocumentStore store,
    InvoiceNumbering numbering,
    InvoiceIssuers issuers,
    IVatIdValidator vatIds,
    IEnumerable<IPlaceOfSupplyRule> rules,
    IEnumerable<IInvoiceLineFactsSource> factsSources,
    Reference.VatTerritories territories,
    InvoiceAfterIssue afterIssue,
    IOptionsMonitor<SparkCommerceInvoicingOptions> options,
    TimeProvider time,
    ILogger<InvoicingService> logger) : ISparkInvoicing
{
    private SparkCommerceInvoicingOptions Options => options.CurrentValue;

    /// <summary>The calendar date of <paramref name="instant"/> in the invoicing zone (K10: converted by the core).</summary>
    private DateOnly IssueDay(DateTimeOffset instant)
        => DateOnly.FromDateTime(MintPlayer.Spark.Services.SparkTimeZones.ToZone(instant, MintPlayer.Spark.Services.SparkTimeZones.FindOrUtc(Options.TimeZone)).DateTime);

    private IReadOnlyList<IPlaceOfSupplyRule> Rules => rules.Any() ? rules.ToList() : BuiltInPlaceOfSupplyRules.All;

    // ---- invoices and receipts ---------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Invoice>> IssueForOrderAsync(CheckoutOrder order, CancellationToken cancellationToken = default)
    {
        if (order.Id is null || order.ConfirmedAt is null || order.ExcludedFromRevenue || order.Total.IsZero)
            return [];

        var active = order.Lines.Where(l => l.Status != OrderLineStatus.Refused).ToList();
        if (active.Count == 0)
            return [];

        var billing = await LoadBillingAsync(order.Id, cancellationToken);
        var buyer = await BuyerAsync(order, billing, cancellationToken);
        var language = buyer.Language;

        // Lines per issuer; extras (donation, insurance, other) go to the platform's document when there is one (it
        // collected them), else to the first; an order-level discount is spread over the item lines pro rata (§4.6).
        var drafts = new Dictionary<string, List<InvoiceDraftLine>>(StringComparer.Ordinal);
        var itemDrafts = new List<(string Issuer, int Index)>();
        foreach (var line in active)
        {
            var facts = await FactsAsync(order, line, cancellationToken);
            var key = issuers.KeyFor(order, line);
            if (!drafts.TryGetValue(key, out var list))
                drafts[key] = list = [];
            var feeTotal = line.Fees.Where(f => f.Mandatory).Sum(f => f.Amount.ToMinorUnits());
            var title = line.Title?.GetValue(language) is { Length: > 0 } t ? t : line.ItemId;
            var supplyDate = facts?.SupplyDate ?? (line.ServiceDate is { } sd ? IssueDay(sd) : null);
            var isGoods = facts?.IsGoods ?? line.IsPhysicalGoods;
            itemDrafts.Add((key, list.Count));
            list.Add(new InvoiceDraftLine(line.LineId, InvoiceLineSource.Item, title, line.Quantity, line.Total.ToMinorUnits() - feeTotal,
                facts?.VatCategory ?? line.VatCategory, isGoods, facts?.EventCountry, line.Channel, supplyDate, line.VatRateBasisPoints));
            foreach (var fee in line.Fees.Where(f => f.Mandatory && !f.Amount.IsZero))
                list.Add(new InvoiceDraftLine(line.LineId + "#" + fee.Code, InvoiceLineSource.Fee, title + " — " + fee.Code, 1, fee.Amount.ToMinorUnits(),
                    Options.FeeVatCategory, false, null, line.Channel, supplyDate, null));
        }

        var extrasTarget = drafts.ContainsKey(InvoiceIssuers.PlatformKey) ? InvoiceIssuers.PlatformKey : drafts.Keys.First();
        var discount = order.Extras.Where(e => e.Kind == PricedExtraKind.Discount).Sum(e => e.Amount.ToMinorUnits());
        if (discount != 0)
        {
            var weights = itemDrafts.Select(d => drafts[d.Issuer][d.Index].Gross).ToList();
            var parts = VatMath.Spread(discount, weights);
            for (var k = 0; k < itemDrafts.Count; k++)
            {
                var (issuer, index) = itemDrafts[k];
                drafts[issuer][index] = drafts[issuer][index] with { Gross = drafts[issuer][index].Gross + parts[k] };
            }
        }
        var outsideScopeNotes = new List<string>();
        foreach (var extra in order.Extras.Where(e => e.Kind != PricedExtraKind.Discount && !e.Amount.IsZero))
        {
            // EN 16931 BR-O-11: a document with a "not subject to VAT" group holds no other group, so a business invoice
            // (Peppol) leaves outside-scope extras (a donation, an insurer's premium) out; they are no supply of the
            // issuer. A consumer's receipt shows them, for the buyer's overview.
            if (buyer.Business && Options.GetCategory(extra.VatCategory).Kind == VatCategoryKind.OutsideScope)
            {
                outsideScopeNotes.Add($"Not invoiced (outside the scope of VAT): {extra.LabelKey} {extra.Amount}");
                continue;
            }
            drafts[extrasTarget].Add(new InvoiceDraftLine("extra:" + extra.Code, InvoiceLineSource.Extra, extra.LabelKey, 1, extra.Amount.ToMinorUnits(),
                extra.VatCategory, false, null, null, null, null));
        }

        var issued = new List<Invoice>();
        foreach (var (issuerKey, lines) in drafts)
        {
            var issuer = await issuers.GetAsync(issuerKey, cancellationToken);
            var kind = buyer.Business || buyer.InvoiceRequested || !Options.ReceiptsForConsumers ? InvoiceKind.Invoice : InvoiceKind.Receipt;
            var id = Invoice.IdFor(kind, order.Id, "order:" + issuerKey);
            var now = time.GetUtcNow();
            var (invoice, created) = await numbering.IssueAsync(id, issuerKey, kind, IssueDay(now).Year, async session =>
            {
                var built = await BuildAsync(session, issuer, buyer, lines, order.Currency, now, cancellationToken);
                built.OrderId = order.Id;
                built.OrderNumber = order.Number;
                built.BuyerUserId = order.BuyerUserId;
                built.SourceKey = "order:" + issuerKey;
                built.SellerOrganizationId = issuer.OrganizationId;
                built.Paid = Money.FromMinorUnits(built.TotalGross.ToMinorUnits(), order.Currency);
                built.SupplyDate = lines.Select(l => l.SupplyDate).Where(d => d is not null).DefaultIfEmpty(IssueDay(order.ConfirmedAt!.Value)).Max();
                if (issuerKey == extrasTarget)
                    built.Notes.AddRange(outsideScopeNotes);
                return built;
            }, cancellationToken);
            if (invoice is not null)
            {
                issued.Add(invoice);
                if (created)
                    await afterIssue.RunAsync(invoice, order.BuyerEmail, cancellationToken);
            }
        }
        return issued;
    }

    /// <summary>
    /// Builds a document from its draft lines inside the numbering unit of work: place of supply per line, the
    /// distance-sales running total (Art. 59c), VAT per group (S21), the mentions.
    /// </summary>
    private async Task<Invoice> BuildAsync(
        IAsyncDocumentSession session,
        InvoiceIssuer issuer,
        InvoiceBuyer buyer,
        IReadOnlyList<InvoiceDraftLine> lines,
        string currency,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var year = IssueDay(now).Year;
        var ioss = !issuer.InEu && issuer.IossNumber is { Length: > 0 }
            && lines.Where(l => l.IsGoods).Sum(l => l.Gross) <= Options.IossCeilingMinorUnits && currency == "EUR";

        // Art. 59c: decide first as if below the threshold, count this supply, then decide again if it crosses.
        InvoiceDistanceSales? current = null;
        var destination = issuer.DestinationTaxationOption;
        if (issuer.InEu && !destination)
        {
            current = await session.LoadAsync<InvoiceDistanceSales>(InvoiceDistanceSales.IdFor(issuer.Key, year), cancellationToken);
            var previous = await session.LoadAsync<InvoiceDistanceSales>(InvoiceDistanceSales.IdFor(issuer.Key, year - 1), cancellationToken);
            destination = previous?.NetMinorUnits > Options.DistanceSales.ThresholdMinorUnits || current?.ExceededAt is not null;
        }

        var decided = lines.Select(l => Decide(l, issuer, buyer, destination, ioss)).ToList();
        var crossBorder = CrossBorderNet(lines, decided, issuer, currency);
        if (issuer.InEu && !destination && crossBorder > 0 && currency == "EUR")
        {
            current ??= new InvoiceDistanceSales { Id = InvoiceDistanceSales.IdFor(issuer.Key, year), IssuerKey = issuer.Key, Year = year };
            if (current.NetMinorUnits + crossBorder > Options.DistanceSales.ThresholdMinorUnits)
            {
                // The supply that exceeds the threshold is itself taxed at destination (Art. 59c(1), second subparagraph).
                current.ExceededAt = now;
                decided = lines.Select(l => Decide(l, issuer, buyer, true, ioss)).ToList();
                crossBorder = CrossBorderNet(lines, decided, issuer, currency);
            }
        }
        if (issuer.InEu && crossBorder > 0 && currency == "EUR")
        {
            current ??= await session.LoadAsync<InvoiceDistanceSales>(InvoiceDistanceSales.IdFor(issuer.Key, year), cancellationToken)
                ?? new InvoiceDistanceSales { Id = InvoiceDistanceSales.IdFor(issuer.Key, year), IssuerKey = issuer.Key, Year = year };
            current.NetMinorUnits += crossBorder;
            await session.StoreAsync(current, current.Id, cancellationToken);
        }

        var basis = buyer.Business ? Options.BusinessPricingBasis : InvoicePricingBasis.Gross;
        var invoice = Assemble(lines, decided, basis, currency);
        invoice.IssuedAt = now;
        invoice.IssueDate = IssueDay(now);
        invoice.Issuer = issuer.Party;
        invoice.Buyer = buyer.Party;
        invoice.Business = buyer.Business;
        invoice.Language = buyer.Language;
        if (ioss)
        {
            invoice.IossNumber = issuer.IossNumber;
            invoice.Notes.Add("IOSS " + issuer.IossNumber);
        }
        return invoice;
    }

    internal sealed record DecidedLine(PlaceOfSupplyDecision Decision, int Rate, string VatCode, VatCategoryOptions Category);

    private DecidedLine Decide(InvoiceDraftLine line, InvoiceIssuer issuer, InvoiceBuyer buyer, bool destination, bool ioss)
    {
        var category = Options.GetCategory(line.Category);
        var context = new PlaceOfSupplyContext
        {
            SupplierCountry = issuer.Country,
            SupplierInEu = issuer.InEu,
            BuyerCountry = buyer.Party.Country,
            BuyerIsBusiness = buyer.Business,
            BuyerVatIdVerified = buyer.Party.VatIdVerified,
            ShipToCountry = line.IsGoods ? buyer.ShipToCountry : null,
            ShipToOutsideVatTerritory = buyer.ShipToOutsideVatTerritory,
            BuyerOutsideVatTerritory = buyer.BuyerOutsideVatTerritory,
            CategoryKind = category.Kind,
            IsGoods = line.IsGoods,
            EventCountry = line.EventCountry,
            DistanceSalesAtDestination = destination,
            IossApplies = ioss,
        };
        var decision = BuiltInPlaceOfSupplyRules.Decide(Rules, context);
        return Classify(decision, category, line, issuer);
    }

    internal static DecidedLine Classify(PlaceOfSupplyDecision decision, VatCategoryOptions category, InvoiceDraftLine line, InvoiceIssuer issuer)
    {
        switch (decision.Treatment)
        {
            case VatTreatment.Taxed:
                var country = decision.TaxCountry ?? issuer.Country;
                int rate;
                if (category.Rates.TryGetValue(country, out var configured))
                    rate = configured;
                else if (string.Equals(country, issuer.Country, StringComparison.OrdinalIgnoreCase) && line.CheckoutRate is { } fromCheckout)
                    rate = fromCheckout;
                else
                    throw new InvalidOperationException($"No VAT rate for category '{line.Category ?? "(default)"}' in {country}: configure Spark:Commerce:Invoicing:VatCategories.");
                return new DecidedLine(decision, rate, rate == 0 ? "Z" : "S", category);
            case VatTreatment.ReverseCharge:
                return new DecidedLine(decision, 0, "AE", category);
            case VatTreatment.IntraCommunityExempt:
                return new DecidedLine(decision, 0, "K", category);
            case VatTreatment.Export:
                return new DecidedLine(decision, 0, "G", category);
            case VatTreatment.Exempt:
                return new DecidedLine(decision with { Mention = category.ExemptionReason ?? decision.Mention, ExemptionReasonCode = category.ExemptionReasonCode ?? decision.ExemptionReasonCode }, 0, "E", category);
            default:
                return new DecidedLine(decision with { Mention = category.ExemptionReason ?? decision.Mention, ExemptionReasonCode = category.ExemptionReasonCode ?? decision.ExemptionReasonCode ?? "VATEX-EU-O" }, 0, "O", category);
        }
    }

    /// <summary>The net value of the B2C goods lines dispatched to another member state (counted toward Art. 59c).</summary>
    private static long CrossBorderNet(IReadOnlyList<InvoiceDraftLine> lines, IReadOnlyList<DecidedLine> decided, InvoiceIssuer issuer, string currency)
    {
        long net = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (decided[i].Decision.Rule is "distance-sale" or "distance-sale-below-threshold")
                net += lines[i].Gross - VatMath.VatFromGross(lines[i].Gross, decided[i].Rate);
        }
        return net;
    }

    /// <summary>Lines, VAT per group (S21) and totals of a document.</summary>
    internal static Invoice Assemble(IReadOnlyList<InvoiceDraftLine> lines, IReadOnlyList<DecidedLine> decided, InvoicePricingBasis basis, string currency)
    {
        var keys = decided.Select(d => (d.VatCode, d.Rate, Country: d.Decision.TaxCountry ?? "", Code: d.Decision.ExemptionReasonCode ?? "")).ToList();
        var vat = VatMath.SplitVat(lines.Select(l => l.Gross).ToList(), keys, decided.Select(d => d.Rate).ToList(), basis);
        var invoice = new Invoice { Currency = currency, PricingBasis = basis };
        for (var i = 0; i < lines.Count; i++)
        {
            var d = decided[i];
            invoice.Lines.Add(new InvoiceLine
            {
                Position = i + 1,
                SourceLineId = lines[i].SourceLineId,
                Source = lines[i].Source,
                Title = lines[i].Title,
                Quantity = lines[i].Quantity,
                Gross = Money.FromMinorUnits(lines[i].Gross, currency),
                Vat = Money.FromMinorUnits(vat[i], currency),
                Net = Money.FromMinorUnits(lines[i].Gross - vat[i], currency),
                VatCategory = lines[i].Category,
                VatCode = d.VatCode,
                VatRateBasisPoints = d.Rate,
                TaxCountry = d.Decision.TaxCountry,
                PlaceOfSupplyRule = d.Decision.Rule,
                ExemptionReason = d.VatCode is "S" or "Z" ? null : d.Decision.Mention,
                ExemptionReasonCode = d.VatCode is "S" or "Z" ? null : d.Decision.ExemptionReasonCode,
                Channel = lines[i].Channel,
                IsGoods = lines[i].IsGoods,
            });
            if (d.Decision.Mention is { Length: > 0 } mention && !invoice.Notes.Contains(mention))
                invoice.Notes.Add(mention);
        }
        Totals(invoice);
        return invoice;
    }

    /// <summary>The VAT breakdown and totals from the lines.</summary>
    internal static void Totals(Invoice invoice)
    {
        var currency = invoice.Currency;
        invoice.VatBreakdown = invoice.Lines
            .GroupBy(l => (l.VatCode, l.VatRateBasisPoints, l.TaxCountry, l.ExemptionReasonCode))
            .Select(g => new InvoiceVatGroup
            {
                VatCode = g.Key.VatCode,
                RateBasisPoints = g.Key.VatRateBasisPoints,
                TaxCountry = g.Key.TaxCountry,
                Taxable = Money.Sum(g.Select(l => l.Net), currency),
                Vat = Money.Sum(g.Select(l => l.Vat), currency),
                ExemptionReason = g.First().ExemptionReason,
                ExemptionReasonCode = g.Key.ExemptionReasonCode,
            })
            .ToList();
        invoice.TotalNet = Money.Sum(invoice.Lines.Select(l => l.Net), currency);
        invoice.TotalVat = Money.Sum(invoice.Lines.Select(l => l.Vat), currency);
        invoice.TotalGross = Money.Sum(invoice.Lines.Select(l => l.Gross), currency);
    }

    // ---- credit notes ----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Invoice>> CreditForOrderAsync(CheckoutOrder order, string sourceKey, CancellationToken cancellationToken = default)
    {
        if (order.Id is null)
            return [];
        var documents = (await GetForOrderAsync(order.Id, cancellationToken)).Where(i => !i.IsCredit).ToList();
        if (documents.Count == 0)
            return [];

        // What the buyer got back that belongs to invoiced lines: refunds and lost disputes, less the refused lines
        // (refunded without ever being invoiced, D1/D3/D40).
        var refused = order.Lines.Where(l => l.Status == OrderLineStatus.Refused).Sum(l => l.Total.ToMinorUnits());
        var invoicedGross = documents.Sum(d => d.TotalGross.ToMinorUnits());
        long refunded = order.State == OrderState.Refunded
            ? order.Total.ToMinorUnits()
            : (order.AmountRefunded?.ToMinorUnits() ?? 0) + (order.State == OrderState.DisputeLost ? order.Disputes.Sum(d => d.Amount.ToMinorUnits()) : 0);
        refunded = Math.Clamp(refunded - refused, 0, invoicedGross);

        // One vector over every line of every document, so returned lines and the pro-rata rest are decided once.
        var returnedLines = order.Lines.Where(l => l.Status == OrderLineStatus.Returned).Select(l => l.LineId).ToHashSet(StringComparer.Ordinal);
        var all = documents.SelectMany(d => d.Lines.Select(l => (Doc: d, Line: l))).ToList();
        var states = new Dictionary<string, InvoiceCreditState?>(StringComparer.Ordinal);
        using (var session = store.OpenAsyncSession())
        {
            foreach (var d in documents)
                states[d.Id!] = await session.LoadAsync<InvoiceCreditState>(InvoiceCreditState.IdFor(d.Id!), cancellationToken);
        }
        var gross = all.Select(x => x.Line.Gross.ToMinorUnits()).ToList();
        var full = refunded >= invoicedGross;
        // Extras (a donation) are refunded only with the whole order (GC3): no share until then.
        var returned = all.Select(x => full || returnedLines.Contains(BaseLineId(x.Line.SourceLineId))).ToList();
        var shares = all.Select(x => x.Line.Source != InvoiceLineSource.Extra || full).ToList();
        var credited = all.Select(x => states[x.Doc.Id!]?.Lines.FirstOrDefault(c => c.Position == x.Line.Position)?.Gross ?? 0).ToList();
        var targets = VatMath.CumulativeRefund(gross, returned, shares, credited, refunded);
        if (targets is null)
            return [];

        var notes = new List<Invoice>();
        foreach (var document in documents)
        {
            var wanted = all.Select((x, i) => (x, i)).Where(p => p.x.Doc == document)
                .ToDictionary(p => p.x.Line.Position, p => targets[p.i]);
            var note = await IssueCreditAsync(document, sourceKey, null, wanted, order.BuyerEmail, cancellationToken);
            if (note is not null)
                notes.Add(note);
        }
        return notes;
    }

    public async Task<IReadOnlyList<Invoice>> CreditRefusedLineAsync(CheckoutOrder order, string lineId, CancellationToken cancellationToken = default)
    {
        if (order.Id is null)
            return [];
        var notes = new List<Invoice>();
        foreach (var document in (await GetForOrderAsync(order.Id, cancellationToken)).Where(i => !i.IsCredit))
        {
            var positions = document.Lines.Where(l => BaseLineId(l.SourceLineId) == lineId).Select(l => l.Position).ToHashSet();
            if (positions.Count == 0)
                continue;
            var note = await IssueCreditAsync(document, "line-refused:" + lineId, new Dictionary<int, long>(), null, order.BuyerEmail, cancellationToken,
                reason: "Line refused at confirmation; refunded", clampTo: positions);
            if (note is not null)
                notes.Add(note);
        }
        return notes;
    }

    public async Task<IReadOnlyList<Invoice>> CreditRefundAsync(CheckoutOrder order, Checkout.Refunds.OrderRefundIssued refund, CancellationToken cancellationToken = default)
    {
        if (order.Id is null)
            return [];
        var documents = (await GetForOrderAsync(order.Id, cancellationToken)).Where(i => !i.IsCredit).ToList();
        var notes = new List<Invoice>();
        foreach (var document in documents)
        {
            var amounts = new Dictionary<int, long>();
            var settleFully = new HashSet<int>();
            foreach (var line in document.Lines)
            {
                long amount = 0;
                var full = false;
                if (line.Source == InvoiceLineSource.Item && refund.Lines.FirstOrDefault(r => r.LineId == line.SourceLineId) is { } r)
                {
                    amount = r.PriceGross.ToMinorUnits();
                    full = r.CumulativeGrossBefore.ToMinorUnits() + r.Gross.ToMinorUnits() + r.Deducted.ToMinorUnits() >= r.LineGross.ToMinorUnits() && r.Deducted.IsZero;
                }
                else if (line.Source == InvoiceLineSource.Fee && line.SourceLineId.Split('#', 2) is [var lineId, var code]
                    && refund.Lines.FirstOrDefault(x => x.LineId == lineId) is { } withFee)
                {
                    amount = withFee.Fees.Where(f => f.Code == code).Sum(f => f.Gross.ToMinorUnits());
                }
                else if (line.Source == InvoiceLineSource.Extra && refund.Extras.FirstOrDefault(e => "extra:" + e.Code == line.SourceLineId) is { } extra)
                {
                    amount = extra.Gross.ToMinorUnits();
                    full = extra.CumulativeGrossBefore.ToMinorUnits() + extra.Gross.ToMinorUnits() >= extra.ExtraGross.ToMinorUnits();
                }
                if (amount > 0)
                    amounts[line.Position] = amount;
                if (full)
                    settleFully.Add(line.Position);
            }
            if (amounts.Count == 0 && settleFully.Count == 0)
                continue;
            var note = await IssueCreditAsync(document, "refund:" + refund.RefundId, amounts, null, order.BuyerEmail, cancellationToken,
                clampTo: settleFully);
            if (note is not null)
                notes.Add(note);
        }
        return notes;
    }

    public async Task<Invoice?> IssueCreditNoteAsync(CreditNoteRequest request, CancellationToken cancellationToken = default)
    {
        Invoice? document;
        using (var session = store.OpenAsyncSession())
            document = await session.LoadAsync<Invoice>(request.InvoiceId, cancellationToken);
        if (document is null || document.IsCredit)
            throw new InvalidOperationException($"'{request.InvoiceId}' is not an invoice or receipt.");
        var amounts = request.Amounts.ToDictionary(a => a.Key, a => a.Value.ToMinorUnits());
        return await IssueCreditAsync(document, request.SourceKey, amounts, null, null, cancellationToken, request.Reason);
    }

    /// <summary>
    /// One credit note on <paramref name="document"/>: either explicit <paramref name="amounts"/> (per position, this
    /// note) or cumulative <paramref name="targets"/> (per position, credited in total afterwards). Strategy H inside
    /// the numbering unit of work, against the credit state read there.
    /// </summary>
    private async Task<Invoice?> IssueCreditAsync(
        Invoice document,
        string sourceKey,
        IReadOnlyDictionary<int, long>? amounts,
        IReadOnlyDictionary<int, long>? targets,
        string? buyerEmail,
        CancellationToken cancellationToken,
        string? reason = null,
        IReadOnlySet<int>? clampTo = null)
    {
        // clampTo set: the amounts come from an event (M9's refund) and are clamped to what a line can still credit,
        // and the positions in it are credited to their end; unset, an amount above that is refused.
        var kind = document.Kind == InvoiceKind.FeeInvoice ? InvoiceKind.FeeCreditNote : InvoiceKind.CreditNote;
        var id = Invoice.IdFor(kind, document.Id!, sourceKey);
        var now = time.GetUtcNow();
        var (note, created) = await numbering.IssueAsync(id, document.IssuerKey, kind, IssueDay(now).Year, async session =>
        {
            var stateId = InvoiceCreditState.IdFor(document.Id!);
            var state = await session.LoadAsync<InvoiceCreditState>(stateId, cancellationToken)
                ?? new InvoiceCreditState { Id = stateId, InvoiceId = document.Id! };
            var lines = document.Lines;
            var lineGross = lines.Select(l => l.Gross.ToMinorUnits()).ToList();
            var lineVat = lines.Select(l => l.Vat.ToMinorUnits()).ToList();
            var creditedGross = lines.Select(l => state.Lines.FirstOrDefault(c => c.Position == l.Position)?.Gross ?? 0).ToList();
            var creditedVat = lines.Select(l => state.Lines.FirstOrDefault(c => c.Position == l.Position)?.Vat ?? 0).ToList();
            var refund = new long[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                var position = lines[i].Position;
                refund[i] = amounts is not null
                    ? amounts.GetValueOrDefault(position)
                    : Math.Max(0, (targets?.GetValueOrDefault(position) ?? 0) - creditedGross[i]);
                if (clampTo is not null)
                    refund[i] = clampTo.Contains(position) ? lineGross[i] - creditedGross[i] : Math.Clamp(refund[i], 0, lineGross[i] - creditedGross[i]);
                if (refund[i] < 0 || creditedGross[i] + refund[i] > lineGross[i])
                    throw new InvalidOperationException($"Line {position} of {document.Number} cannot be credited {refund[i]} more (credited {creditedGross[i]} of {lineGross[i]}).");
            }
            if (refund.All(r => r == 0))
                return null;

            var groups = lines.Select(l => (l.VatCode, l.VatRateBasisPoints, l.TaxCountry ?? "", l.ExemptionReasonCode ?? "")).ToList();
            var vat = VatMath.CreditVat(lineGross, lineVat, groups, refund, creditedGross, creditedVat);

            var credit = new Invoice
            {
                Currency = document.Currency,
                PricingBasis = document.PricingBasis,
                IssuedAt = now,
                IssueDate = IssueDay(now),
                Language = document.Language,
                OrderId = document.OrderId,
                OrderNumber = document.OrderNumber,
                BuyerUserId = document.BuyerUserId,
                SellerOrganizationId = document.SellerOrganizationId,
                SourceKey = sourceKey,
                CreditedInvoiceId = document.Id,
                CreditedInvoiceNumber = document.Number,
                CreditedInvoiceDate = document.IssueDate,
                // The parties as on the credited document: a credit note corrects that document (Art. 219).
                Issuer = document.Issuer,
                Buyer = document.Buyer,
                Business = document.Business,
                IossNumber = document.IossNumber,
                Notes = [.. document.Notes],
                SupplyDate = document.SupplyDate,
            };
            if (reason is { Length: > 0 })
                credit.Notes.Insert(0, reason);
            for (var i = 0; i < lines.Count; i++)
            {
                if (refund[i] == 0)
                    continue;
                var source = lines[i];
                credit.Lines.Add(new InvoiceLine
                {
                    Position = credit.Lines.Count + 1,
                    CreditedPosition = source.Position,
                    SourceLineId = source.SourceLineId,
                    Source = source.Source,
                    Title = source.Title,
                    Quantity = refund[i] == lineGross[i] ? source.Quantity : 1,
                    Gross = Money.FromMinorUnits(refund[i], document.Currency),
                    Vat = Money.FromMinorUnits(vat[i], document.Currency),
                    Net = Money.FromMinorUnits(refund[i] - vat[i], document.Currency),
                    VatCategory = source.VatCategory,
                    VatCode = source.VatCode,
                    VatRateBasisPoints = source.VatRateBasisPoints,
                    TaxCountry = source.TaxCountry,
                    PlaceOfSupplyRule = source.PlaceOfSupplyRule,
                    ExemptionReason = source.ExemptionReason,
                    ExemptionReasonCode = source.ExemptionReasonCode,
                    Channel = source.Channel,
                    IsGoods = source.IsGoods,
                });
                var c = state.Line(source.Position);
                c.Gross += refund[i];
                c.Vat += vat[i];
            }
            Totals(credit);
            credit.Paid = credit.TotalGross;
            state.CreditNoteIds.Add(id);
            await session.StoreAsync(state, stateId, cancellationToken);

            // Credit notes on distance sales reduce the Art. 59c total of the year they are issued in (net).
            var crossBorder = credit.Lines.Where(l => l.PlaceOfSupplyRule is "distance-sale" or "distance-sale-below-threshold").Sum(l => l.Net.ToMinorUnits());
            if (crossBorder > 0 && credit.Currency == "EUR")
            {
                var totalId = InvoiceDistanceSales.IdFor(document.IssuerKey, IssueDay(now).Year);
                var total = await session.LoadAsync<InvoiceDistanceSales>(totalId, cancellationToken)
                    ?? new InvoiceDistanceSales { Id = totalId, IssuerKey = document.IssuerKey, Year = IssueDay(now).Year };
                total.NetMinorUnits -= crossBorder;
                await session.StoreAsync(total, totalId, cancellationToken);
            }
            return credit;
        }, cancellationToken);

        if (note is not null && created)
            await afterIssue.RunAsync(note, buyerEmail ?? document.Buyer.Email, cancellationToken);
        return note;
    }

    // ---- fee invoices ------------------------------------------------------------------------------------------------

    public async Task<Invoice?> IssueFeeInvoiceAsync(string sellerOrganizationId, int year, int month, CancellationToken cancellationToken = default)
    {
        var from = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddMonths(1);
        List<PlatformFeeEntry> entries;
        using (var session = store.OpenAsyncSession())
        {
            entries = await session.Query<PlatformFeeEntry>()
                .Where(e => e.SellerOrganizationId == sellerOrganizationId && e.OccurredAtUtc >= from && e.OccurredAtUtc < to)
                .Take(10_000)
                .ToListAsync(cancellationToken);
        }
        var byCurrency = entries.GroupBy(e => e.Amount.Currency).ToList();
        if (byCurrency.Count == 0)
            return null;
        if (byCurrency.Count > 1)
            logger.LogWarning("Seller {Seller} has platform fees in several currencies in {Year}-{Month}; one fee invoice per currency is not supported, the first currency is invoiced.", sellerOrganizationId, year, month);
        var currency = byCurrency[0].Key;
        var net = byCurrency[0].Sum(e => e.SignedMinorUnits);
        if (net <= 0)
            return null; // fee refunds beyond the month's fees are netted in a later month (guide).

        var platform = issuers.Platform();
        var sellerAsIssuer = await issuers.GetAsync(sellerOrganizationId, cancellationToken);
        var verified = false;
        string? checkReference = null;
        if (VatIdFormat.Parse(sellerAsIssuer.Party.VatId) is { } vatId)
        {
            var check = await vatIds.CheckAsync(vatId, cancellationToken);
            verified = check.Valid;
            checkReference = check.RequestIdentifier;
        }
        var buyer = new InvoiceBuyer(new InvoiceParty
        {
            Name = sellerAsIssuer.Party.Name,
            VatId = sellerAsIssuer.Party.VatId,
            VatIdVerified = verified,
            VatCheckReference = checkReference,
            CompanyId = sellerAsIssuer.Party.CompanyId,
            Country = sellerAsIssuer.Country,
            AddressLines = sellerAsIssuer.Party.AddressLines,
            Email = sellerAsIssuer.Party.Email,
            PeppolId = sellerAsIssuer.Party.PeppolId,
        }, Business: true, InvoiceRequested: true, null, false, false, Options.DefaultLanguage);
        var period = $"{year:D4}-{month:D2}";
        var lines = new List<InvoiceDraftLine>
        {
            new("fees:" + period, InvoiceLineSource.PlatformFee, "Platform fees " + period, 1, net, Options.FeeVatCategory, false, null, null, DateOnly.FromDateTime(to.AddDays(-1)), null),
        };
        var id = Invoice.IdFor(InvoiceKind.FeeInvoice, sellerOrganizationId, "fees:" + period);
        var now = time.GetUtcNow();
        var (invoice, created) = await numbering.IssueAsync(id, InvoiceIssuers.PlatformKey, InvoiceKind.FeeInvoice, IssueDay(now).Year, async session =>
        {
            var built = await BuildAsync(session, platform, buyer, lines, currency, now, cancellationToken);
            built.SourceKey = "fees:" + period;
            built.SellerOrganizationId = sellerOrganizationId;
            built.SupplyDate = DateOnly.FromDateTime(to.AddDays(-1));
            built.Paid = built.TotalGross; // fees are withheld from the payouts
            return built;
        }, cancellationToken);
        if (invoice is not null && created)
            await afterIssue.RunAsync(invoice, sellerAsIssuer.Party.Email, cancellationToken);
        return invoice;
    }

    // ---- reads -------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<Invoice>> GetForOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var list = await session.Query<Invoice>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
            .Where(i => i.OrderId == orderId)
            .Take(1024)
            .ToListAsync(cancellationToken);
        return list.OrderBy(i => i.IssuedAt).ThenBy(i => i.Number, StringComparer.Ordinal).ToList();
    }

    public async Task<bool> SaveBillingDetailsAsync(InvoiceBillingDetails details, CancellationToken cancellationToken = default)
    {
        if ((await GetForOrderAsync(details.OrderId, cancellationToken)).Any(i => !i.IsCredit))
            return false;
        details.Id = InvoiceBillingDetails.IdFor(details.OrderId);
        details.UpdatedAt = time.GetUtcNow();
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(details, details.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private async Task<InvoiceBillingDetails?> LoadBillingAsync(string orderId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<InvoiceBillingDetails>(InvoiceBillingDetails.IdFor(orderId), cancellationToken);
    }

    private async Task<InvoiceLineFacts?> FactsAsync(CheckoutOrder order, CheckoutOrderLine line, CancellationToken cancellationToken)
    {
        foreach (var source in factsSources)
        {
            if (await source.GetAsync(order, line, cancellationToken) is { } facts)
                return facts;
        }
        return null;
    }

    /// <summary>The buyer party: the billing details when given (VIES-checked), else the order's name and e-mail.</summary>
    internal async Task<InvoiceBuyer> BuyerAsync(CheckoutOrder order, InvoiceBillingDetails? billing, CancellationToken cancellationToken)
    {
        var party = new InvoiceParty
        {
            Name = billing?.Name ?? order.BuyerName ?? "",
            Email = order.BuyerEmail,
            CompanyId = billing?.CompanyId,
            Country = billing?.Country?.ToUpperInvariant(),
            AddressLines = billing?.AddressLines ?? [],
            PostalCode = billing?.PostalCode,
            City = billing?.City,
            PeppolId = billing?.PeppolId,
        };
        var business = false;
        if (VatIdFormat.Parse(billing?.VatId) is { } vatId)
        {
            party.VatId = vatId.ToString();
            business = true;
            var check = await vatIds.CheckAsync(vatId, cancellationToken);
            party.VatIdVerified = check.Valid;
            party.VatCheckReference = check.RequestIdentifier;
            party.Country ??= VatIdFormat.IsoCountry(vatId);
        }
        else if (billing?.CompanyId is { Length: > 0 })
            business = true;

        var shipTo = billing?.ShipToCountry?.ToUpperInvariant() ?? party.Country;
        var shipToTerritory = await territories.FindAsync(shipTo, billing?.ShipToPostalCode ?? billing?.PostalCode, cancellationToken);
        var billingTerritory = await territories.FindAsync(party.Country, party.PostalCode, cancellationToken);
        party.Territory = billingTerritory?.Code;
        return new InvoiceBuyer(party, business, billing?.InvoiceRequested ?? false, shipTo,
            shipToTerritory?.OutsideVatTerritory ?? false, billingTerritory?.OutsideVatTerritory ?? false,
            billing?.Language is { Length: > 0 } lang ? lang : Options.DefaultLanguage);
    }

    /// <summary>A fee line's source id is <c>{lineId}#{fee}</c>; the order line is the part before.</summary>
    private static string BaseLineId(string sourceLineId)
    {
        var hash = sourceLineId.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? sourceLineId : sourceLineId[..hash];
    }
}
