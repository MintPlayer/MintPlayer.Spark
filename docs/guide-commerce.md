# Commerce core (`MintPlayer.Spark.Commerce`)

The base of the commerce suite (#521, PRD `docs/commerce_PRD.md` §4.1, §4.2, §4.15, §4.19, §4.20). Inventory,
Payments, Checkout, Shop and Ticketing build on it. Contracts are in `MintPlayer.Spark.Commerce.Abstractions`.

> ⚠️ **Not legal advice.** The rules below cite the EU texts the PRD's gap analysis named. Every EU citation was
> **checked against the official text on EUR-Lex on 2026-10-20** (D37); the compliance table in [guide-commerce-compliance.md](guide-commerce-compliance.md) lists the article, the consolidated
> version read, the member-state options and the points still open. National transpositions were not checked.

## Setup

```csharp
spark.AddAuthentication<SparkUser>();
spark.AddCommerce();             // also adds Organizations, Approvals, Cron
```

```jsonc
"Spark": { "Commerce": {
  "Merchant": "ConnectedSellers",                 // or Platform (D5)
  "Country": "BE",                                // picks the retention floors
  "Sellers": { "Registration": "ApprovalRequired" },
  "Channels": {
    "tickets": {
      "Allocation": "HoldFromSelection",
      "Hold": { "Default": "00:10:00", "Min": "00:05:00", "Max": "00:20:00" },
      "MaxPaymentWindow": "00:30:00",
      "RequireSignIn": true,
      "Fees": [ { "Code": "service", "Kind": "Service", "Fixed": { "EUR": 1.50 } } ]
    },
    "shop": { "Allocation": "OnPayment", "SellsGoods": true,
              "RequiredLegalDocuments": [ "terms-of-sale", "privacy-policy", "withdrawal-information" ] }
  }
} }
```

```jsonc
// App_Data/security.json
"bindings": {
  "commerce:platform-admins": ["Administrators"],   // ManageSellers, ViewTraderData, DecideNotices
  "commerce:finance": ["Finance"],                  // ViewTraderData
  "commerce:notice-reviewers": ["Moderators"]       // DecideNotices (Moderation's Review/Moderation works too)
}
```

**Precedence: configuration wins.** `AddCommerce(o => …)` calls (the host's and other packages') are defaults applied
in call order; the `Spark:Commerce` section is bound after them (§4.2).

## Channels and bounds (D2)

Each channel holds the allocation mode, hold bounds, payment window, sign-in, waiting-room modes, refund-policy
bounds, fees, resale switches, deceptive measures, country rules and required legal documents. A seller's per-sale
choice is clamped when read (`DurationBounds.Clamp`, `IntBounds.Clamp`), so tightening the configuration applies at once.

## The final price (default 4, round 5)

`FinalPrice = unit price + VAT + every mandatory fee` — computed only by `IFinalPriceCalculator`
(Price Indication Directive 98/6/EC Art. 2(a); CRD 2011/83/EU Art. 6(1)(e)). Integer cents, each division rounded once
half away from zero (`Money.RoundDiv`, spike S21); a percentage fee is rounded per unit, so one unit × N equals N units.

- `ICommercePriceDisplay.DisplayAsync(item, announcedReductionSince)` builds what a listing shows: final price,
  breakdown, Omnibus prior price, trader label. Storefront packages call it after loading an item the caller may read.
- `GET /spark/commerce/price?channel=&amount=&currency=&basis=&vat=&quantity=` quotes a raw price.
- `<spark-commerce-price [price] [breakdown]>` renders it; a prior price the server sends is always shown.

## Price history and the Omnibus prior price (GA3)

Every price change of an `ISellableItem` saved through Spark is appended to `CommercePriceChanges/{itemId}/{ticks}`
with an audit entry, in the same commit (an interceptor; a new item gets its first entry right after its commit).
`ISparkPriceHistory.GetLowestFinalPriceAsync(itemId, before)` is the lowest final price in force during the 30 days
before a reduction (PID Art. 6a(2), inserted by Directive (EU) 2019/2161; window `Pricing:PriorPriceWindow`). For
progressive reductions pass the start of the first step. Perishable-goods exemptions (Art. 6a(3)) and progressive
reductions (Art. 6a(5)) are member-state options (checked against EUR-Lex 2026-10-20); the national texts were not checked.

## Sellers (D21, GA5, GA6, GB14)

A seller is an Organizations organization with a `SellerProfiles/{organizationId}` document.

| `Sellers:Registration` | Flow |
|---|---|
| `Closed` | only platform admins register sellers |
| `ApprovalRequired` | submitting raises `commerce.seller-registration`; a platform admin approves (this also records the manual trader check) |
| `Open` | approved at once; a trader's data goes to `commerce.trader-verification`. With `Merchant=Platform` refused unless `AcknowledgeOpenPlatformMerchant` |

`ISparkSellers.CanSellAsync(organizationId, channel)` is the **open-sale gate**: approved, not suspended, trader status
declared, trader data verified (traders), KYC verified (connected mode, through `ISellerKycSource` from Payments),
sales not closed for DAC7 (`commerce.seller.dac7Blocked`, D43), and the channel's required legal documents published.
`CanReceivePayoutsAsync` answers KYC verified and not suspended; the DAC7 block is not a payout reason (D43).

- **Trader traceability (DSA Reg. (EU) 2022/2065 Art. 30).** Name, address, phone, e-mail, identity document
  (attachment, downloadable only with `ViewTraderData/Commerce`, every download audited), payment account, trade
  register and number, self-certification (Art. 30(1)(a)–(e)); best-effort verification (Art. 30(2)) through
  Approvals; public trader page `GET /spark/commerce/traders?organizationId=` (Art. 30(7)); data erased 6 months after
  the relationship ends (Art. 30(5)), when the organization is deleted. Switching it off needs
  `AcknowledgeDsaArt29Exemption` (Art. 29, contested).
- **Trader status** on every listing (CRD Art. 6a(1)(b)); `<spark-commerce-trader-label>`.
- **Ranking parameters** (CRD Art. 6a(1)(a); P2B Reg. (EU) 2019/1150 Art. 5): storefront packages register
  `IRankingParameterSource`; `GET /spark/commerce/ranking-parameters`, `<spark-commerce-ranking-parameters>`.
- **P2B terms and complaints** (P2B Art. 3, 11): the current platform terms and DPA are accepted at registration
  (version stored); `POST /spark/commerce/sellers/complaint` raises `commerce.seller-complaint` with a deadline.
  `AcknowledgeP2BSmallEnterprise` switches complaints off (Art. 11(5)).
- **DAC7** (Directive (EU) 2021/514, Annex V): TIN and country, VAT id, birth date (individuals), primary address,
  business registration number (entities), IBAN and holder. A daily job reminds; after two reminders and at least
  60 days the seller's **sales close** (D43, owner 2026-10-19): `CanSellAsync` refuses with `commerce.seller.dac7Blocked`
  until the data is complete, and completing it reopens them; both steps are audited (`commerce.dac7-sales-block`) and
  the seller is notified (`sales-blocked`). Payouts at the provider are not touched: under Connect the provider pays
  the seller out directly. Paragraph and national transposition to confirm.
- **Data roles (GB14).** Connected sellers are controllers of their buyers' data, the platform a processor (GDPR
  Art. 28): the DPA is a legal document accepted at registration. `GET /spark/commerce/sellers/export?kind=buyers`
  serves an `ISellerExportSource`'s rows as CSV (formula cells defused) to Admin/Owner or the `finance`/`seller-admin`
  roles, and audits every export.

## Legal documents (GA2, GA10)

`LegalDocuments/{guid}`: kind (`terms-of-sale`, `privacy-policy`, `withdrawal-information`, `platform-terms`, `dpa`,
`accessibility-statement`, `cookie-policy`, or the host's own), owner (seller or platform), title and text per language,
version, effective date, optional PDF. Publishing assigns the next version and freezes the document; a new version is a
new draft. `ISparkLegalDocuments.GetInForceAsync` resolves the seller's own, then the platform's, per kind; Checkout
stores the refs on the order (M7). Startup warns about a channel whose platform documents are missing and about a
missing accessibility statement (Directive (EU) 2019/882 Annex V).

## Notice and action (GA5)

`POST /spark/commerce/notices` — anyone, signed in or a visitor (DSA Art. 16(1)): reason, explanation, location,
optional name and e-mail, required good-faith statement (Art. 16(2)). Packages that own reportable content register
`IDsaNoticeTargetResolver`; a signed-in reporter's notice on an `IModeratable` target is also flagged in Moderation.
Moderation's reviewers or `DecideNotices/Commerce` decide (`POST /spark/commerce/admin/notices/decide`); an action
issues a statement of reasons (Art. 17(3): restriction, facts, notice-based, automation, legal or contractual ground,
redress). `GET /spark/commerce/admin/notices/report` gives the transparency figures (Art. 15(1)(b), 24). Submitting
statements to the Commission's transparency database (Art. 24(5)) is not implemented.

## Audit and retention (§4.15)

`ICommerceAudit.RecordAsync(session, entry)` appends `CommerceAudit/{subject}/{ticks}-{guid}` in the caller's session.
Every money-moving package uses it. Retention per document type (`Retention:Periods`), never below the floor:

| Type | Floor | Basis (to confirm) |
|---|---|---|
| invoices, orders, payments, money-audit | 10 years | BE: VAT Code art. 60 §3, Code of Economic Law art. III.86; elsewhere VAT Directive Art. 247 leaves it to the member state |
| dac7-records | 5 years | DAC7 Annex V: at least 5, at most 10 years (startup warns above 10) |
| trader-data | 6 months after the relationship | DSA Art. 30(5) |
| dsa-notices, seller-exports | 1 year | no statutory floor found; one transparency-report year |
| price-history | 30 days | PID Art. 6a(2) |

`Retention:ConfirmedFloors` replaces a floor with the host counsel's value (logged). Issued invoices are never altered:
the invoicing package deletes them whole when the floor ends.

## Startup validation (§4.2)

Refused: `Open` registration with `Platform` merchant unless acknowledged; `RandomizedPreQueue`/`Ballot` without
sign-in unless `WaitingRoom:AllowWithoutSignIn`; an enabled deceptive measure without a trigger; a consent-requiring
feature without the consent primitive (GA9); `Basket:MixSellers` without `Basket:AcknowledgeSplitMerchantOfRecord`,
or when no registered provider can split (D41; see "Mixed-seller baskets" below); a
country-keyed rule on nationality, residence, card-issuing country or IP country that refuses, redirects or changes
conditions (Geo-blocking Reg. (EU) 2018/302 Art. 3–5; delivery-address zones allowed; configuration only, custom code is
not judged) (GA11); trader traceability or complaint handling off without the acknowledgement; a retention period
below its floor; invalid bounds; duplicate fee codes; an unknown currency.
Warned: a per-buyer limit where guests may buy; connected mode without a KYC source; a channel without its legal
documents; no accessibility statement; with `MixSellers`, a registered provider that cannot split (its methods are
never offered to a mixed-seller basket).

### Mixed-seller baskets (D40, D41)

In `ConnectedSellers` mode every single-seller order is a direct charge on the seller's own provider account, and the
platform never holds the seller's money (D5). A basket with lines of **several** sellers can only be paid once by
splitting that payment, and both split mechanisms route the money through the platform: Mollie routing creates the
payment on the platform's account and routes shares to the connected organizations, and Stripe separate charges and
transfers charge the platform's account and transfer each seller's share afterwards. For such an order the
**platform is the merchant of record**.

It is therefore off by default and takes two settings plus a split-capable provider:

```json
"Spark": { "Commerce": {
  "Merchant": "ConnectedSellers",
  "Basket": { "MixSellers": true, "AcknowledgeSplitMerchantOfRecord": true }
} }
```

and `Spark:Commerce:Payments:Mollie:SplitPayments` or `Spark:Commerce:Payments:Stripe:SeparateChargesAndTransfers` on
(see the payments guide, §9).

> ⚠️ **Licensing and KYC.** Collecting money on behalf of other sellers is a regulated activity in the EU (payment
> services under PSD2, Directive (EU) 2015/2366, whose commercial-agent exclusion in Art. 3(b) covers only an agent
> "authorised … on behalf of only the payer or only the payee"; checked against EUR-Lex 02015L2366-20250117, 2026-10-20) and brings anti-money-laundering duties. Before switching this on, the host's counsel confirms that the platform qualifies for an
> exemption or that the provider's contract (Mollie split payments, Stripe Connect separate charges) covers it, and that
> the host's own KYC with the provider is at the level the provider demands for holding funds. As merchant of record the
> platform also answers to the buyer for the order: refunds and chargebacks of a split payment start on the platform's
> account. Confirm per country (D37).

## Consent seam (X21, minimal)

X21 is not implemented yet. Core has the seam GA9's validation needs: `ISparkConsent`, `SparkConsentPurposes`,
`services.RequiresConsent(purpose, requiredBy)`. With no `ISparkConsent` registered, any declared requirement (and a
payments adapter's `RequiresConsentPurpose`) refuses startup.

## Client (`@mintplayer/ng-spark/commerce/core`)

`SparkCommerceService`, `<spark-commerce-price>`, `<spark-commerce-trader-label>`, `<spark-commerce-trader-page>`,
`<spark-commerce-ranking-parameters>`, `<spark-commerce-legal-documents>`, `<spark-commerce-notice-form>`, and
`sparkCommerceRoutes()` (`traders/:organizationId`, `ranking`, `legal`, `legal/:organizationId`).
