# Commerce: invoicing

`MintPlayer.Spark.Commerce.Invoicing` (alias `commerce-invoicing`, ng entry `@mintplayer/ng-spark/commerce/invoicing`) issues
receipts, invoices and credit notes for Checkout's orders, computes VAT per line, sends business invoices over Peppol and
produces the periodic exports (OSS/IOSS, DAC7, accounting). PRD: `docs/commerce_PRD.md` §4.8, D19, §13 round 3 (S21) and
rounds 5–6.

> **Liability.** Every rule is configurable and nothing here is tax advice (§4.8). The articles cited were checked against
> Council Directive 2006/112/EC on EUR-Lex (consolidated 2025-04-14, CELEX 02006L0112-20250414) and Implementing
> Regulation 282/2011 (02011R0282-20250414) on 2026-10-20 (D37). Your member state's transposition and options were
> not checked: have them checked before production. Not modelled: admission to an event attended **virtually** is taxed
> where the customer is (Art. 53 second paragraph, Art. 54(1) second subparagraph), and **Monaco** counts as France
> (Art. 7(2)); add a rule for either if you need it.

## Setup

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddCommerceInvoicing()        // adds Checkout
        .UseBillitPeppol()              // or .UseStorecovePeppol(), .UseTestPeppol(); optional
        .UseReferenceDataVat();         // optional: TEDB suggestions + VAT territories from the reference data
});
```

```json
"Spark": { "Commerce": { "Invoicing": {
  "Platform": { "Name": "Example BV", "VatId": "BE0123456749", "CompanyId": "0123456749", "Country": "BE",
                "AddressLines": [ "Voorbeeldstraat 1" ], "PostalCode": "9000", "City": "Gent", "PeppolId": "0208:0123456749" },
  "TimeZone": "Europe/Brussels",
  "VatCategories": {
    "standard":  { "Rates": { "BE": 2100, "NL": 2100, "DE": 1900, "FR": 2000 } },
    "admission": { "Kind": "Admission", "IsService": true, "Rates": { "BE": 600, "NL": 900, "FR": 550 } },
    "service":   { "IsService": true, "Rates": { "BE": 2100, "NL": 2100 } },
    "donation":  { "Kind": "OutsideScope", "ExemptionReason": "Donation, no consideration", "ExemptionReasonCode": "VATEX-EU-O" }
  },
  "Peppol": { "Provider": "billit" },
  "Dac7": { "PlatformTin": "0123456749", "TicketChannels": [ "tickets" ] }
} } }
```

Startup refuses: no categories, an unknown default or fee category, an exempt/outside-scope category without a reason, a
`Platform` merchant without an issuer name and VAT id, an unknown or ambiguous Peppol provider, an unknown time zone,
duplicate series prefixes.

A sellable item names its category (`CheckoutItem.VatCategory`); Checkout stores it on the order line. Rates are basis
points per member state. Nothing is hard-coded.

## How it plugs in

Invoicing is Checkout's `IOrderEventRecipient` (coupling rule §4.1: Checkout never references it):

| Event | What Invoicing does |
|---|---|
| `Placed` | Renders a PDF for every accepted legal-document version that has none (GA2, below) |
| `Confirmed` | Issues one document per issuer: a **receipt** for a consumer, an **invoice** for a business (a VAT id or company id) or a consumer who asked for one |
| `RefundIssued` (M9) | One **credit note** per invoice and refund id, from `OrderEvent.Refund`: per line the price part, the fee parts and the extras; a line the refund settles in full is credited to its end |
| `LineRefused` (M7) | A refused line is never invoiced; only a line already on an invoice is credited in full (key: the line id) |
| `DisputeLost` (GB1) | A credit note for what the chargeback took (cumulative, pro rata) |
| `Refunded` | Ignored: M9 raises `RefundIssued` for every refund it pays, before `Refunded`; crediting both would credit twice |

Everything is idempotent: a document's id derives from its cause (order + issuer, invoice + refund id, …).

**Line facts.** Checkout does not store where an event takes place. Ticketing (or the host) registers an
`IInvoiceLineFactsSource` answering `InvoiceLineFacts(EventCountry, IsGoods, VatCategory, SupplyDate)` per order line;
without one, a line is a service in the supplier's country unless Checkout marked it physical goods.

**Billing details.** Checkout's order has no billing address. The buyer posts it for an order not invoiced yet:
`POST /spark/commerce/invoicing/billing` (`SparkBillingDetailsRequest`: company name, VAT id, address, Peppol id, language,
"I need an invoice"). Once a document is issued, the details are refused (`alreadyInvoiced`): an issued invoice is never
changed.

## Issuers and numbering (D19)

- **Issuer:** the platform in `Platform` mode and for D41 mixed-seller orders (merchant of record); each seller in
  `ConnectedSellers` mode, from its trader data (Commerce `SellerProfile`, GA5) and its `SellerInvoicingProfile`
  (`SellerInvoicingProfiles/{organizationId}`: establishment country, Peppol id, IBAN, OSS registration, the Art. 59c(3)
  option, an IOSS number). The platform also issues the **fee invoices** to sellers (monthly job, from the fee ledger).
- **Gap-free numbers** per issuer, series and year: `{prefix}{year}-{sequence}` (`F` invoice, `R` receipt, `C` credit
  note, `P`/`PC` fee invoice and its credit note; 6 digits by default). The counter is a compare-exchange claim (X12) and
  the document is stored in the **same cluster-wide unit of work**: a number exists exactly when its document does. A
  concurrent issuer's commit fails and retries with the next number; tests issue 20 at once and get 1..20.
- **Dates:** the invoice date (BT-2) and the numbering year are the calendar date in `TimeZone` (default
  `Europe/Brussels`), converted by the core (`SparkTimeZones`, K10).
- **Never altered** (§4.15): a document is written once; `ContentHash` is its SHA-256 as issued. The Peppol state, the
  credited amounts and the mail live in other documents.

## VAT: place of supply (GA4)

`IPlaceOfSupplyRule` per line; the first rule that answers wins. A host adds its own (an electronic-service rule under
Art. 58, a domestic reverse charge under Art. 194) by registering one.

| Order | Rule | Result | Basis (checked 2026-10-20) |
|---|---|---|---|
| 100 | Category `Exempt` / `OutsideScope` | `E` / `O` with the category's reason | host configuration; Art. 226(11) |
| 200 | Admission (`Kind: Admission`) | Taxed where the event takes place, B2C and B2B; outside the EU: `O` | Art. 53 (B2B), Art. 54(1) (B2C) |
| 300 | IOSS (GC8): non-EU supplier, goods to an EU consumer, consignment ≤ €150, IOSS number | Taxed at destination, IOSS number on the invoice | Art. 33(c), Art. 369l–369x |
| 400 | Export: goods dispatched outside the EU or into a territory outside the VAT territory | `G`, 0 %, "Exempt export" | Art. 146(1)(a)/(b), Art. 6 |
| 400 | B2B services to a customer outside the EU | `O` | Art. 44 |
| 500 | Goods to a business in another member state, VAT id **valid in VIES** | `K`, 0 %, "Exempt intra-Community supply" | Art. 138(1) (valid id is substantive since Directive (EU) 2018/1910) |
| 600 | Services to a business in another member state, VIES-valid id | `AE`, 0 %, mention **"Reverse charge"** | Art. 44, Art. 196, Art. 226(11a) |
| 700 | Goods to a consumer in another member state | Destination once the supplier's EU-wide distance sales exceed **€10,000** this year (from the supply that crosses it) or last year, or by the supplier's option; else the supplier's country | Art. 33(a), Art. 59c(1)–(3) |
| 1000 | Everything else | The supplier's member state | Art. 45 |

- **VIES.** A B2B VAT id is checked through VIES' REST API (`POST /check-vat-number`), positive answers cached 7 days, the
  consultation number kept on the invoice. An unavailable member state (`MS_UNAVAILABLE`) is no answer: the id stays
  unverified and VAT is charged (Implementing Regulation 282/2011 Art. 18 lets a supplier treat an unverified customer as a
  non-taxable person, Art. 18(2); checked 2026-10-20).
- **The €10,000 threshold** is tracked per issuer and calendar year (`InvoiceDistanceSales`, updated in the numbering unit
  of work, so each document counts once; credit notes reduce it). Only EUR orders count; a non-EUR seller's conversion
  (Art. 91(2)) is not done.
- **VAT territories** (Art. 6; Northern Ireland as `XI` for goods): `Spark:Commerce:Invoicing:Territories` (country,
  code, postcode pattern), plus the reference data's country profiles with `UseReferenceDataVat()`; configured entries win.
- **Northern Ireland** (`XI`) counts as a member state for goods (Windsor Framework, Protocol Art. 8).

## VAT: amounts (S21)

Invoicing never changes an amount: it splits what the buyer paid into net and VAT.

- VAT is rounded **once per rate group** and split over the group's lines by largest remainder (`Money.RoundDiv`,
  `Money.Allocate`).
- Consumers' receipts extract VAT from the gross. Business invoices use `BusinessPricingBasis = Net` (default): the group
  net is chosen so that `VAT = round(net × rate)` exactly whenever an integer net exists (EN 16931 BR-CO-17 exact),
  otherwise VAT lies within one cent, which the CEN Schematron's BR-CO-17 tolerates (±1).
- An order discount is spread over the item lines pro rata before VAT (§4.6).
- **Credit notes mirror the invoice lines (strategy H):** per line the cumulative-difference VAT, clamped to what the line
  can still credit, then nudged a cent at a time toward the group's cumulative target. A full refund cancels the invoice
  exactly, line by line and group by group (S21: 40,000/40,000; the tests replay 2,000 generated invoices).
- Outside-scope extras (a donation, an insurer's premium) appear on a consumer's receipt but are left off a business
  invoice (EN 16931 BR-O-11 forbids mixing "not subject" with other groups), with a note.

## E-invoicing (Peppol BIS Billing 3.0)

- `UblWriter` writes UBL 2.1 `Invoice` (380) / `CreditNote` (381) with the Peppol customization and profile ids, BT-10
  (the order number), BG-3 (the credited invoice), delivery date and deliver-to country, VAT breakdown with VATEX codes.
- **Receivers:** the buyer's Peppol id from the billing details; for a Belgian VAT id, the enterprise number it implies
  (`0208:…`); other schemes are never guessed. The access point's directory is asked first (`DiscoverReceivers`).
- **Validation.** The official artefacts (CEN/TC 434 EN 16931 UBL Schematron under EUPL 1.2; OpenPeppol BIS 3 Schematron)
  are **XSLT 2.0**. .NET runs XSLT 1.0 only and the .NET XSLT 2.0 processor (Saxon) is not MIT-licensed, so they are not
  vendored. Instead: `En16931RuleSubset` checks the rules this library's documents can break (BR-01…BR-11, BR-16, BR-55,
  BR-CO-10/13/14/15/16/17, BR-S-*, BR-Z-*, BR-AE-*, BR-IC-*, BR-G-*, BR-E-*, BR-O-*, PEPPOL-EN16931-R003/R010/R020), and
  every registered `IEInvoiceValidator` runs too: plug the official Schematron in there (a phive/Saxon service, the
  Peppol testbed). Both access points also validate on submission. A violation blocks the transmission (the PDF still
  goes out) and is listed on the `InvoiceTransmission` (`ValidationErrors`).
- **Transmission** (`InvoiceTransmission`, query `InvoiceTransmissions`): `Pending` → `Sent`, or `NotReachable` (no Peppol
  id / not in the directory), or `Failed` (refused, invalid, or `MaxAttempts` outages). Claimed with optimistic concurrency
  before each attempt; retried every 10 minutes with backoff.

### Access points (Q-O2, spike S14)

Q-O2 is still open in the PRD; two adapters with public API documentation ship, plus a test adapter:

| Package | Why | API used (doc-derived, round 4) |
|---|---|---|
| `.Invoicing.Peppol.Billit` | Belgian access point; Belgium's B2B mandate since 2026-01-01 | `POST v1/peppol/sendxml` (`{ "XML": … }`), `GET v1/peppol/participantInformation/{id}` |
| `.Invoicing.Peppol.Storecove` | Widely used developer-facing access point, EU-wide | `POST document_submissions` (raw UBL), `POST discovery/receives` |
| `.Invoicing.Peppol.Test` | Development and tests: records, simulates refusals and outages | — |

Fixtures are in `tests/MintPlayer.Spark.Commerce.Tests/Invoicing/Fixtures` (README lists what is confirmed and what is not).
Sandbox runs with the owner's keys remain to be done.

## PDFs (Q-O9)

A small dependency-free PDF writer (`MinimalPdf`: PDF 1.4, standard Helvetica, WinAnsi) renders invoices
(`IInvoicePdfRenderer`, replaceable) with labels in en/nl/fr/de. Characters outside Windows-1252 print as `?`; a document in
another script needs a replacement renderer with an embedded font. Chosen because `libs/` had no PDF dependency,
QuestPDF's community licence has revenue limits and headless Chromium is heavy (PRD Q-O9).

- `GET /spark/commerce/invoicing/pdf?token=` — the signed link mailed with every document (Data Protection, 90 days), for
  guests; `?id=` for signed-in readers under the row rule.
- `GET /spark/commerce/invoicing/documents?number=` — an order's documents (`SparkInvoiceSummary[]`).
- **GA2:** on `Placed`, each accepted legal-document version without a PDF gets one rendered and attached as Commerce's
  `document.pdf` (`HasPdf`), so Checkout's confirmation mail attaches it. Invoicing's `ICheckoutMailAttachments`
  contributor renders any that are still missing right before the confirmation is queued, and adds the withdrawal
  receipt as a PDF (GA1). The invoice mail (`SparkInvoicing/InvoiceIssued`) attaches the document's PDF and keeps the
  signed link for downloading it again.

## Exports

All read issued documents only, so every figure traces to a document number. Nothing is filed for you.

| Export | Endpoint | Content |
|---|---|---|
| Union OSS (GA4) | `GET /exports/oss?issuer=&year=&quarter=` | CSV `MemberStateOfConsumption, SupplyType, VatRatePercent, TaxableAmount, VatAmount, Currency, CorrectionOfPeriod`: B2C supplies taxed in another member state (Art. 369f quarterly, Art. 369g content). Credit notes on an earlier quarter are separate rows with `CorrectionOfPeriod` (Implementing Regulation 282/2011 Art. 61) |
| IOSS (GC8) | `…/exports/oss?…&ioss=true&quarter=` or `&month=` | Same columns for IOSS documents; the IOSS return is **monthly** (Art. 369s) |
| DAC7 (GA6) | `GET /exports/dac7?year=&format=csv\|xml` (platform only) | Per seller: TIN, VAT id, birth date, address, IBAN, consideration / activities / fees **per quarter**; excluded sellers (< 30 activities and ≤ €2,000, Annex V Section I B(4)(d)) **marked, not dropped**; ticket sales flagged "probably out of scope" unless `Dac7:TicketSalesInScope` (D37). XML in the shape of the OECD DPI XML Schema v1.0 (`urn:oecd:ties:dpi:v1`); validate against your member state's XSD before filing (confirm, D37) |
| Accounting (GB4) | `GET /exports/accounting?issuer=&from=&to=` | Zip: `sales-journal.csv` (`Date, Number, Kind, Order, CreditedNumber, Buyer, BuyerVatId, BuyerCountry, VatCode, VatRatePercent, TaxCountry, TaxableAmount, VatAmount, GrossAmount, Currency, PlaceOfSupplyRule`; credit notes negative), `payments-journal.csv` (the settlement lines of §4.5: `Date, Provider, SettlementId, SettlementReference, LineId, Kind, SourceId, Gross, Fee, Net, Currency, Match, MatchedDocumentId`), `ubl/{number}.xml` |

CSV: comma, CRLF, UTF-8 with BOM, invariant numbers; text cells starting with `= + - @` are prefixed with `'` (CSV
injection). `issuer` is a seller's organization id or `platform`.

## TEDB rate suggestions (GC10)

With `UseReferenceDataVat()` and the reference-data client's sync on (collection `VatRateSuggestions`),
`GET /spark/commerce/invoicing/vat-rate-suggestions?country=` lists the Commission's TEDB rates beside the host's configured
rate of the same category. **Suggestions only:** nothing changes a configured rate.

## Retention (§4.8, §4.15)

Documents are kept for Commerce's `invoices` period, never below its legal floor (Belgium: 10 years per the research;
the national period was not checked, D37: VAT Directive Art. 247(1) leaves it to each member state), counted from issue. The daily retention job then **deletes them whole** (with their credit state,
transmission and billing details); until then they are never anonymised or changed.

## Access

`QueryRead/Invoice` and `QueryRead/InvoiceTransmission` for `@authenticated`, narrowed per row: the buyer their own; a
seller's Owners, Admins and `finance` members (configurable `FinanceRole`) the seller's; the platform organization
(`Spark:Commerce:Payments:PlatformOrganizationId`) everything. Exports need the seller scope or the platform's; DAC7 the
platform's.

## Known limits

- Status updates from the access points (Storecove webhooks, Billit inbox polling) are not consumed: `Sent` means the access
  point accepted the document.
- A consumer who asks for an invoice after the receipt was issued is not handled (the receipt is the document; a host can
  credit it and issue anew through `IssueCreditNoteAsync`).
- Electronic services (Art. 58) and member-state reverse-charge options (Art. 194) need a host rule.
- Fee invoices: one currency per seller and month; fee refunds beyond the month's fees are netted in a later month.
