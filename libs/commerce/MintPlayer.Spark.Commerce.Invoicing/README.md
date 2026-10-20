# MintPlayer.Spark.Commerce.Invoicing

Invoicing for the MintPlayer.Spark commerce suite (commerce PRD §4.8, D19). Guide: `docs/guide-commerce-invoicing.md`.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddCommerceInvoicing()          // Spark:Commerce:Invoicing (adds Checkout)
        .UseStorecovePeppol()             // or .UseBillitPeppol(), .UseTestPeppol(); optional
        .UseReferenceDataVat();           // optional: TEDB rate suggestions + VAT territories (GC10, GA4)
});
```

- **Numbering:** gap-free per issuer, series and year: the counter (a compare-exchange claim, X12) and the document commit
  in one cluster-wide unit of work. Series: `F` invoices, `R` receipts, `C` credit notes, `P`/`PC` fee invoices.
- **Documents:** receipts (B2C), invoices (B2B or on request), credit notes mirroring the invoice lines (S21 strategy H),
  platform-fee invoices to sellers. **Never altered** (SHA-256 `ContentHash`); deleted whole when the retention period
  (Commerce's `invoices` floor) ends.
- **VAT:** host-defined categories and rates per country; `IPlaceOfSupplyRule` per line (admission where the event is,
  B2B reverse charge, intra-Community goods, exports, distance sales with the €10,000 threshold, IOSS, VAT territories);
  VIES checks; B2B invoices on a Net basis with VAT per rate group (BR-CO-17).
- **E-invoicing:** EN 16931 / Peppol BIS Billing 3.0 UBL, validated by a built-in rule subset plus any
  `IEInvoiceValidator`, sent through an `IPeppolAccessPoint`.
- **Exports:** Union OSS and IOSS returns (CSV), DAC7 (CSV and DPI XML), accounting export (sales and payments journals +
  UBL batch, zip).
- **PDFs:** a dependency-free PDF writer (Helvetica, WinAnsi); legal-document versions get a PDF rendering (GA2).

Plugs into Checkout as an `IOrderEventRecipient` (`Confirmed`, M9's `RefundIssued`, `DisputeLost`, `Placed`); Checkout
never references it. Rights: `QueryRead/Invoice` and `QueryRead/InvoiceTransmission` for `@authenticated`, narrowed per row
(the buyer, the seller's Owners/Admins/finance, the platform organization).
