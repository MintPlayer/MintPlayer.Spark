using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Invoicing;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;
using MintPlayer.Spark.Commerce.Invoicing.Exports;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using MintPlayer.Spark.Commerce.Invoicing.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Tests.Invoicing;

/// <summary>Invoicing over RavenDB: numbering, issuing, credit notes, the threshold, Peppol and the exports.</summary>
public class InvoicingFlowTests : SparkTestDriver
{
    private InvoicingHost Host(Action<SparkCommerceInvoicingOptions>? configure = null, Action<SparkCommerceOptions>? commerce = null, params string[] validVatIds)
        => InvoicingHost.Build(Store, configure, commerce, validVatIds);

    private async Task SaveBillingAsync(InvoicingHost host, string orderId, string? vatId = null, string country = "BE", string? shipTo = null, bool invoice = false, string? peppolId = null)
        => (await host.Invoicing.SaveBillingDetailsAsync(new InvoiceBillingDetails
        {
            OrderId = orderId, Name = vatId is null ? "A. Buyer" : "Klant BV", VatId = vatId, Country = country, AddressLines = ["Kerkstraat 2"],
            PostalCode = "1000", City = "Brussel", ShipToCountry = shipTo, InvoiceRequested = invoice, PeppolId = peppolId,
        })).Should().BeTrue();

    // ---- numbering -------------------------------------------------------------------------------------------------

    /// <summary>D19: gap-free per issuer and year, under concurrency: 20 orders issued at once get 1..20, each once.</summary>
    [Fact]
    public async Task Numbers_are_gap_free_and_unique_under_concurrent_issuing()
    {
        var host = Host();
        var orders = Enumerable.Range(1, 20).Select(i => InvoicingHost.Order($"CheckoutOrders/N{i}", ("tickets/a", 12.10m, 1, false, "standard"))).ToList();

        var issued = await Task.WhenAll(orders.Select(o => host.Invoicing.IssueForOrderAsync(o)));

        var sequences = issued.SelectMany(x => x).Select(i => i.Sequence).Order().ToList();
        sequences.Should().Equal(Enumerable.Range(1, 20).Select(i => (long)i));
        issued.SelectMany(x => x).Select(i => i.Number).Distinct().Should().HaveCount(20);
        issued.SelectMany(x => x).Should().AllSatisfy(i => i.Number.Should().StartWith("R2026-"));
    }

    [Fact]
    public async Task Issuing_twice_returns_the_same_document_and_uses_no_number()
    {
        var host = Host();
        var order = InvoicingHost.Order("CheckoutOrders/I1", ("tickets/a", 10m, 1, false, "standard"));
        var first = (await host.Invoicing.IssueForOrderAsync(order)).Single();
        var again = (await host.Invoicing.IssueForOrderAsync(order)).Single();
        again.Number.Should().Be(first.Number);
        var next = (await host.Invoicing.IssueForOrderAsync(InvoicingHost.Order("CheckoutOrders/I2", ("tickets/a", 10m, 1, false, "standard")))).Single();
        next.Sequence.Should().Be(first.Sequence + 1);
    }

    [Fact]
    public async Task An_issued_document_is_stored_with_its_hash_and_never_changes()
    {
        var host = Host();
        var invoice = (await host.Invoicing.IssueForOrderAsync(InvoicingHost.Order("CheckoutOrders/H1", ("tickets/a", 10m, 1, false, "standard")))).Single();
        using var session = Store.OpenAsyncSession();
        var stored = await session.LoadAsync<Invoice>(invoice.Id);
        InvoiceHash.IsIntact(stored).Should().BeTrue();
        (await host.Invoicing.SaveBillingDetailsAsync(new InvoiceBillingDetails { OrderId = "CheckoutOrders/H1", VatId = "BE0123456749" })).Should().BeFalse();
    }

    // ---- receipts, invoices, VAT ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_consumer_gets_a_receipt_with_VAT_extracted_from_what_they_paid()
    {
        var host = Host();
        var receipt = (await host.Invoicing.IssueForOrderAsync(InvoicingHost.Order("CheckoutOrders/C1", ("shop/shirt", 24.20m, 2, true, "standard")))).Single();
        receipt.Kind.Should().Be(InvoiceKind.Receipt);
        receipt.TotalGross.Should().Be(new Money(24.20m, "EUR"));
        receipt.TotalVat.Should().Be(new Money(4.20m, "EUR"));
        receipt.Lines.Single().TaxCountry.Should().Be("BE");
    }

    [Fact]
    public async Task GA4_admission_is_taxed_in_the_event_country()
    {
        var host = Host();
        host.Facts.Facts["tickets/paris"] = new InvoiceLineFacts(EventCountry: "FR");
        var receipt = (await host.Invoicing.IssueForOrderAsync(InvoicingHost.Order("CheckoutOrders/A1", ("tickets/paris", 105.50m, 1, false, "admission")))).Single();
        var line = receipt.Lines.Single();
        line.TaxCountry.Should().Be("FR");
        line.VatRateBasisPoints.Should().Be(550);
        line.PlaceOfSupplyRule.Should().Be("admission");
    }

    [Fact]
    public async Task GA4_a_verified_business_in_another_member_state_is_reverse_charged_and_sent_over_Peppol()
    {
        var host = Host(validVatIds: "NL123456789B01");
        var order = InvoicingHost.Order("CheckoutOrders/B1", ("services/workshop", 121m, 1, false, "service"));
        await SaveBillingAsync(host, order.Id!, vatId: "NL123456789B01", country: "NL", peppolId: "0106:12345678");

        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();

        invoice.Kind.Should().Be(InvoiceKind.Invoice);
        invoice.Lines.Single().VatCode.Should().Be("AE");
        invoice.TotalVat.IsZero.Should().BeTrue();
        invoice.Notes.Should().Contain("Reverse charge");
        invoice.Buyer.VatIdVerified.Should().BeTrue();
        host.Peppol.Submissions.Should().ContainSingle();
        host.Peppol.Submissions.Single().Request.Receiver.Should().Be(new PeppolParticipant("0106", "12345678"));
    }

    [Fact]
    public async Task An_unverified_VAT_id_is_charged_VAT_like_a_consumer_and_still_gets_an_invoice()
    {
        var host = Host();
        var order = InvoicingHost.Order("CheckoutOrders/B2", ("services/workshop", 121m, 1, false, "service"));
        await SaveBillingAsync(host, order.Id!, vatId: "NL123456789B01", country: "NL");
        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();
        invoice.Kind.Should().Be(InvoiceKind.Invoice);
        invoice.Lines.Single().VatCode.Should().Be("S");
        invoice.Lines.Single().TaxCountry.Should().Be("BE");
    }

    [Fact]
    public async Task GA4_distance_sales_move_to_the_destination_from_the_supply_that_crosses_the_threshold()
    {
        var host = Host(o => o.DistanceSales.ThresholdMinorUnits = 9_000); // EUR 90 for the test: 50 + 50 net crosses it
        var first = InvoicingHost.Order("CheckoutOrders/D1", ("shop/shirt", 60.50m, 1, true, "standard"));
        await SaveBillingAsync(host, first.Id!, country: "DE", shipTo: "DE");
        (await host.Invoicing.IssueForOrderAsync(first)).Single().Lines.Single().TaxCountry.Should().Be("BE");

        var second = InvoicingHost.Order("CheckoutOrders/D2", ("shop/shirt", 60.50m, 1, true, "standard"));
        await SaveBillingAsync(host, second.Id!, country: "DE", shipTo: "DE");
        var crossing = (await host.Invoicing.IssueForOrderAsync(second)).Single().Lines.Single();
        crossing.TaxCountry.Should().Be("DE");
        crossing.VatRateBasisPoints.Should().Be(1900);
    }

    [Fact]
    public async Task GA4_goods_to_the_Canary_Islands_are_an_export()
    {
        var host = Host();
        var order = InvoicingHost.Order("CheckoutOrders/T1", ("shop/shirt", 20m, 1, true, "standard"));
        await host.Invoicing.SaveBillingDetailsAsync(new InvoiceBillingDetails { OrderId = order.Id!, Country = "ES", ShipToCountry = "ES", ShipToPostalCode = "35001" });
        var line = (await host.Invoicing.IssueForOrderAsync(order)).Single().Lines.Single();
        line.VatCode.Should().Be("G");
        line.Vat.IsZero.Should().BeTrue();
    }

    [Fact]
    public async Task Connected_sellers_each_issue_their_own_documents()
    {
        var host = Host(commerce: c => c.Merchant = MerchantMode.ConnectedSellers);
        var order = InvoicingHost.Order("CheckoutOrders/M1", ("tickets/a", 10m, 1, false, "standard"), ("tickets/b", 20m, 1, false, "standard"));
        order.Lines[0].SellerOrganizationId = "Organizations/1";
        order.Lines[1].SellerOrganizationId = "Organizations/2";
        var documents = await host.Invoicing.IssueForOrderAsync(order);
        documents.Select(d => d.SellerOrganizationId).Order().Should().Equal("Organizations/1", "Organizations/2");
        documents.Should().AllSatisfy(d => d.Sequence.Should().Be(1)); // each seller's own series
    }

    // ---- credit notes ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task M9_refunds_issue_one_credit_note_per_refund_and_a_full_refund_cancels_the_invoice_exactly()
    {
        var host = Host();
        var order = InvoicingHost.Order("CheckoutOrders/R1", ("tickets/a", 33.33m, 3, false, "standard"), ("shop/mug", 9.99m, 1, true, "standard"));
        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();

        var partial = Refund("refunds/1", order, ("l1", 11.11m, 0m, 1));
        var notes = await host.Invoicing.CreditRefundAsync(order, partial);
        var note = notes.Single();
        note.Kind.Should().Be(InvoiceKind.CreditNote);
        note.CreditedInvoiceNumber.Should().Be(invoice.Number);
        note.TotalGross.Should().Be(new Money(11.11m, "EUR"));
        (await host.Invoicing.CreditRefundAsync(order, partial)).Single().Number.Should().Be(note.Number); // idempotent

        var rest = Refund("refunds/2", order, ("l1", 22.22m, 11.11m, 2), ("l2", 9.99m, 0m, 1));
        var final = (await host.Invoicing.CreditRefundAsync(order, rest)).Single();
        (note.TotalVat + final.TotalVat).Should().Be(invoice.TotalVat);
        (note.TotalNet + final.TotalNet).Should().Be(invoice.TotalNet);
    }

    /// <summary>
    /// A discounted order as Checkout stores it (S21): each line carries its share of the order discount, and
    /// Net + Vat = Total − fees + OrderDiscount. Goods 2 × 12.10 and 12.10 less a 3.63 discount: Money.Allocate(363,
    /// [2420, 1210]) = 242 / 121, so the VAT bases are 21.78 and 10.89 and the buyer paid 32.67.
    /// </summary>
    private static CheckoutOrder DiscountedOrder(string id)
    {
        var order = InvoicingHost.Order(id, ("tickets/a", 24.20m, 2, false, "standard"), ("tickets/b", 12.10m, 1, false, "standard"));
        order.Extras.Add(new CheckoutOrderExtra { Code = "basket10", Kind = PricedExtraKind.Discount, Amount = new Money(-3.63m, "EUR"), LabelKey = "promo", Source = "test" });
        foreach (var (line, share) in order.Lines.Zip(new[] { -242L, -121L }))
        {
            line.OrderDiscount = Money.FromMinorUnits(share, "EUR");
            var gross = line.Total.ToMinorUnits() + share;
            var vat = Money.RoundDiv(gross * 2100, 12_100);
            line.VatRateBasisPoints = 2100;
            line.Net = Money.FromMinorUnits(gross - vat, "EUR");
            line.Vat = Money.FromMinorUnits(vat, "EUR");
        }
        order.Total = Money.Sum(order.Lines.Select(l => l.Total).Concat(order.Extras.Select(e => e.Amount)), "EUR");
        return order;
    }

    /// <summary>A refund planned by Checkout's own arithmetic and turned into the payload Invoicing credits.</summary>
    private static OrderRefundIssued PlannedRefund(string id, CheckoutOrder order, CheckoutRefundLedger ledger, params RefundLineRequest[] lines)
    {
        var plan = RefundMath.Plan(order, ledger, new RefundSpec(RefundKind.Cancellation, null, lines));
        var refund = new CheckoutRefund
        {
            RefundId = id, Kind = RefundKind.Cancellation, Status = RefundStatus.Succeeded, Amount = plan.Amount, Deducted = plan.Deducted,
            Parts = [.. plan.Parts], Lines = [.. plan.Lines], IssuedAt = DateTimeOffset.Parse("2026-10-20T10:00:00Z"),
        };
        var issued = RefundEngine.Issued(order, ledger, refund);
        RefundMath.Apply(ledger, plan);
        return issued;
    }

    private static CheckoutRefundLedger LedgerOf(CheckoutOrder order) => new()
    {
        OrderId = order.Id!, Components = RefundMath.Components(order),
        Lines = order.Lines.Select(l => new RefundLedgerLine { LineId = l.LineId, ItemId = l.ItemId }).ToList(),
    };

    /// <summary>
    /// Red before the fix: the order line did not store its order-discount share, so Checkout's refund parts and
    /// Invoicing's invoice lines each re-spread the discount on their own; a full refund now credits exactly what the
    /// invoice declared (gross and VAT), which is exactly what was charged.
    /// </summary>
    [Fact]
    public async Task A_full_refund_of_a_discounted_order_credits_exactly_the_invoiced_gross_and_VAT()
    {
        var host = Host();
        var order = DiscountedOrder("CheckoutOrders/RD1");
        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();
        invoice.TotalGross.Should().Be(order.Total);
        invoice.Lines.Where(l => l.Source == InvoiceLineSource.Item).Select(l => l.Gross.ToMinorUnits()).Should().Equal(2178L, 1089L);

        var ledger = LedgerOf(order);
        var refund = PlannedRefund("refunds/rd1", order, ledger, new RefundLineRequest("l1", 2), new RefundLineRequest("l2", 1));
        refund.Amount.Should().Be(order.Total);
        foreach (var line in refund.Lines)
        {
            var orderLine = order.Lines.Single(l => l.LineId == line.LineId);
            line.PriceGross.Should().Be(orderLine.Net + orderLine.Vat);
        }

        var note = (await host.Invoicing.CreditRefundAsync(order, refund)).Single();
        note.TotalGross.Should().Be(invoice.TotalGross);
        note.TotalVat.Should().Be(invoice.TotalVat);
        note.TotalNet.Should().Be(invoice.TotalNet);
    }

    /// <summary>A partial line refund refunds and credits its discounted share: 1 of 2 units of a 21.78 base is 10.89.</summary>
    [Fact]
    public async Task A_partial_line_refund_of_a_discounted_order_refunds_its_discounted_share()
    {
        var host = Host();
        var order = DiscountedOrder("CheckoutOrders/RD2");
        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();
        var ledger = LedgerOf(order);

        var refund = PlannedRefund("refunds/rd2", order, ledger, new RefundLineRequest("l1", 1));
        refund.Amount.Should().Be(new Money(10.89m, "EUR"));
        var note = (await host.Invoicing.CreditRefundAsync(order, refund)).Single();
        note.TotalGross.Should().Be(new Money(10.89m, "EUR"));

        var rest = PlannedRefund("refunds/rd3", order, ledger, new RefundLineRequest("l1", 1), new RefundLineRequest("l2", 1));
        (refund.Amount + rest.Amount).Should().Be(order.Total);
        var final = (await host.Invoicing.CreditRefundAsync(order, rest)).Single();
        (note.TotalVat + final.TotalVat).Should().Be(invoice.TotalVat);
    }

    [Fact]
    public async Task A_lost_dispute_credits_what_the_chargeback_took()
    {
        var host = Host();
        var order = InvoicingHost.Order("CheckoutOrders/X1", ("tickets/a", 50m, 1, false, "standard"));
        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();
        order.State = OrderState.DisputeLost;
        order.Disputes.Add(new CheckoutOrderDispute { DisputeId = "dp_1", Amount = new Money(50m, "EUR"), Status = "lost" });
        var note = (await host.Invoicing.CreditForOrderAsync(order, "dispute:dp_1")).Single();
        note.TotalGross.Should().Be(invoice.TotalGross);
        note.TotalVat.Should().Be(invoice.TotalVat);
        (await host.Invoicing.CreditForOrderAsync(order, "dispute:dp_1")).Single().Number.Should().Be(note.Number);
    }

    /// <summary>
    /// <c>LineRefused</c> after the invoice (S21 strategy H): the refused line is credited in full, mirroring the invoice
    /// line, keyed by the line id (a redelivered event issues nothing new); the other line is left alone.
    /// </summary>
    [Fact]
    public async Task A_line_refused_after_invoicing_is_credited_in_full_once()
    {
        var host = Host();
        var order = InvoicingHost.Order("CheckoutOrders/LR1", ("tickets/a", 30m, 2, false, "standard"), ("shop/mug", 9.99m, 1, true, "standard"));
        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();
        order.Lines[1].Status = OrderLineStatus.Refused;

        var note = (await host.Invoicing.CreditRefusedLineAsync(order, "l2")).Single();

        note.Kind.Should().Be(InvoiceKind.CreditNote);
        note.CreditedInvoiceNumber.Should().Be(invoice.Number);
        note.SourceKey.Should().Contain("line-refused:l2");
        note.TotalGross.Should().Be(new Money(9.99m, "EUR"));
        note.TotalVat.Should().Be(invoice.Lines.Where(l => l.SourceLineId == "l2").Select(l => l.Vat).Single());
        (await host.Invoicing.CreditRefusedLineAsync(order, "l2")).Single().Number.Should().Be(note.Number); // idempotent
    }

    /// <summary>A line refused before the invoice is never on it, so its refusal credits nothing.</summary>
    [Fact]
    public async Task A_line_refused_before_invoicing_is_left_off_and_credits_nothing()
    {
        var host = Host();
        var order = InvoicingHost.Order("CheckoutOrders/LR2", ("tickets/a", 30m, 2, false, "standard"), ("shop/mug", 9.99m, 1, true, "standard"));
        order.Lines[1].Status = OrderLineStatus.Refused;
        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();
        invoice.Lines.Should().NotContain(l => l.SourceLineId == "l2");

        (await host.Invoicing.CreditRefusedLineAsync(order, "l2")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_explicit_credit_note_above_what_a_line_can_still_credit_is_refused()
    {
        var host = Host();
        var invoice = (await host.Invoicing.IssueForOrderAsync(InvoicingHost.Order("CheckoutOrders/E1", ("tickets/a", 10m, 1, false, "standard")))).Single();
        await ((Func<Task>)(() => host.Invoicing.IssueCreditNoteAsync(new CreditNoteRequest(invoice.Id!, "manual-1",
            new Dictionary<int, Money> { [1] = new Money(10.01m, "EUR") })))).Should().ThrowAsync<InvalidOperationException>();
    }

    private static OrderRefundIssued Refund(string id, CheckoutOrder order, params (string LineId, decimal Gross, decimal Before, int Quantity)[] lines)
        => new(id, order.Id!, "EUR", default, null, new Money(lines.Sum(l => l.Gross), "EUR"), Money.Zero("EUR"),
            lines.Select(l =>
            {
                var line = order.Lines.Single(x => x.LineId == l.LineId);
                return new RefundedLine(l.LineId, line.ItemId, null, l.Quantity, new Money(l.Gross, "EUR"), new Money(l.Gross, "EUR"), [],
                    Money.Zero("EUR"), new Money(l.Before, "EUR"), line.Total, line.Net, line.Vat, line.VatRateBasisPoints, line.VatCategory);
            }).ToList(),
            [], DateTimeOffset.Parse("2026-10-20T10:00:00Z"), false);

    // ---- fee invoices, Peppol, exports -------------------------------------------------------------------------------

    [Fact]
    public async Task A_business_buyer_without_a_Peppol_id_is_not_reachable_and_keeps_the_PDF()
    {
        var host = Host(validVatIds: "NL123456789B01");
        var order = InvoicingHost.Order("CheckoutOrders/P1", ("services/workshop", 121m, 1, false, "service"));
        await SaveBillingAsync(host, order.Id!, vatId: "NL123456789B01", country: "NL");
        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();
        using var session = Store.OpenAsyncSession();
        var transmission = await session.LoadAsync<InvoiceTransmission>(InvoiceTransmission.IdFor(invoice.Id!));
        transmission.Status.Should().Be(InvoiceTransmissionStatus.NotReachable);
        host.Peppol.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Belgian_business_is_reached_through_the_enterprise_number_its_VAT_id_implies()
    {
        var host = Host(validVatIds: "BE0987654321");
        var order = InvoicingHost.Order("CheckoutOrders/P3", ("services/workshop", 121m, 1, false, "service"));
        await SaveBillingAsync(host, order.Id!, vatId: "BE0987654321", country: "BE");
        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();
        invoice.Lines.Single().VatCode.Should().Be("S"); // domestic B2B: Belgian VAT
        host.Peppol.Submissions.Single().Request.Receiver.Should().Be(new PeppolParticipant("0208", "0987654321"));
    }

    [Fact]
    public async Task A_retryable_outage_leaves_the_transmission_pending_for_the_retry_job()
    {
        var host = Host(validVatIds: "NL123456789B01");
        host.PeppolOptions.OutagesRemaining = 1;
        var order = InvoicingHost.Order("CheckoutOrders/P2", ("services/workshop", 121m, 1, false, "service"));
        await SaveBillingAsync(host, order.Id!, vatId: "NL123456789B01", country: "NL", peppolId: "0106:12345678");
        var invoice = (await host.Invoicing.IssueForOrderAsync(order)).Single();

        var dispatcher = host.Get<EInvoiceDispatcher>();
        using (var session = Store.OpenAsyncSession())
            (await session.LoadAsync<InvoiceTransmission>(InvoiceTransmission.IdFor(invoice.Id!))).Status.Should().Be(InvoiceTransmissionStatus.Pending);
        host.Clock.Now += TimeSpan.FromHours(1);
        (await dispatcher.TrySendAsync(InvoiceTransmission.IdFor(invoice.Id!), CancellationToken.None))!.Status.Should().Be(InvoiceTransmissionStatus.Sent);
    }

    [Fact]
    public async Task GA4_the_OSS_return_lists_B2C_supplies_taxed_in_other_member_states_by_country_and_rate()
    {
        var host = Host(o => o.DistanceSales.ThresholdMinorUnits = 0);
        host.Facts.Facts["tickets/paris"] = new InvoiceLineFacts(EventCountry: "FR");
        var goods = InvoicingHost.Order("CheckoutOrders/O1", ("shop/shirt", 119m, 1, true, "standard"));
        await SaveBillingAsync(host, goods.Id!, country: "DE", shipTo: "DE");
        await host.Invoicing.IssueForOrderAsync(goods);
        await host.Invoicing.IssueForOrderAsync(InvoicingHost.Order("CheckoutOrders/O2", ("tickets/paris", 105.50m, 1, false, "admission")));
        await host.Invoicing.IssueForOrderAsync(InvoicingHost.Order("CheckoutOrders/O3", ("tickets/a", 12.10m, 1, false, "standard"))); // domestic: not in OSS
        WaitForIndexing(Store);

        var rows = await host.Get<InvoicingExports>().OssReturnAsync(InvoiceIssuers.PlatformKey, 2026, 4, ioss: false, CancellationToken.None);

        rows.Should().HaveCount(2);
        rows.Single(r => r.MemberState == "DE").Should().Be(new OssReturnRow("DE", "goods", 1900, 10_000, 1_900, "EUR", null));
        rows.Single(r => r.MemberState == "FR").SupplyType.Should().Be("services");
        InvoicingExports.OssCsv(rows, "oss.csv").Content.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GB4_the_accounting_export_holds_both_journals_and_the_UBL_batch()
    {
        var host = Host();
        await host.Invoicing.IssueForOrderAsync(InvoicingHost.Order("CheckoutOrders/G1", ("tickets/a", 12.10m, 1, false, "standard")));
        WaitForIndexing(Store);
        var file = await host.Get<InvoicingExports>().AccountingAsync(InvoiceIssuers.PlatformKey, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), CancellationToken.None);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(file.Content));
        zip.Entries.Select(e => e.FullName).Should().Contain("sales-journal.csv");
        zip.Entries.Select(e => e.FullName).Should().Contain("payments-journal.csv");
        zip.Entries.Any(e => e.FullName.StartsWith("ubl/R2026-", StringComparison.Ordinal)).Should().BeTrue();
    }
}
