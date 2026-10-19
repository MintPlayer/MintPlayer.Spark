# MintPlayer.Spark.Commerce (alias `commerce`)

The core of the commerce suite (`docs/commerce_PRD.md` §4.1, M4). Contracts live in
`MintPlayer.Spark.Commerce.Abstractions`; this package implements them.

```csharp
spark.AddAuthentication<SparkUser>();
spark.AddCommerce();   // binds Spark:Commerce; adds Organizations, Approvals and Cron
```

```jsonc
// App_Data/security.json
"bindings": {
  "commerce:platform-admins": ["Administrators"],
  "commerce:finance": ["Finance"],
  "commerce:notice-reviewers": ["Moderators"]
}
```

| Area | What | PRD |
|---|---|---|
| Channels | `Spark:Commerce:Channels:{name}`: allocation, hold bounds (clamped when read), payment window, sign-in, waiting room, refunds, fees, resale, deceptive measures, required legal documents | D1–D4, D10, D20, D29, D30 |
| Price | `IFinalPriceCalculator` (one `FinalPrice`), `ICommercePriceDisplay` (breakdown, Omnibus prior price, trader label), `GET /spark/commerce/price` | default 4, GA3 |
| Price history | `ISparkPriceHistory`: append-only `CommercePriceChanges/{item}/{ticks}`, lowest final price of the last 30 days | GA3 |
| Sellers | `SellerProfiles/{organizationId}`; registration Closed / ApprovalRequired / Open; KYC gate via `ISellerKycSource`; `ISparkSellers.CanSellAsync` / `CanReceivePayoutsAsync` | D21 |
| Trader traceability | DSA Art. 30 data, identity document (attachment, `ViewTraderData/Commerce` only, audited), best-effort check through Approvals, public trader page, 6-month retention | GA5 |
| Ranking, P2B | `GET /spark/commerce/ranking-parameters` from `IRankingParameterSource`; platform terms accepted at registration; complaints as Approvals requests | GA5 |
| Notice and action | `POST /spark/commerce/notices` (anyone), decisions by Moderation's reviewers or `DecideNotices/Commerce`, statements of reasons, transparency report | GA5 |
| DAC7 | seller tax data, reminders, payout block | GA6 |
| Legal documents | versioned, immutable once published, PDF, in force per seller/platform, acceptance records; sale refused without required documents | GA2 |
| Data roles | DPA accepted at registration; audited seller CSV exports through `ISellerExportSource` | GB14 |
| Audit, retention | `ICommerceAudit` (append-only), retention per type with legal floors (unconfirmed per country) | default 8, §4.15 |
| Startup validation | refusals and warnings of §4.2, incl. GA9, GA10, GA11, D40 | §4.2 |

Endpoints are under `/spark/commerce` (public), `/spark/commerce/sellers` and `/spark/commerce/admin` (signed in).
The client is `@mintplayer/ng-spark/commerce/core`. See `docs/guide-commerce.md`.
