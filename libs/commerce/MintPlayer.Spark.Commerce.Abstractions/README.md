# MintPlayer.Spark.Commerce.Abstractions

The contracts every commerce package and app entity builds on (commerce PRD §4.1). Small and stable: no App_Data
layer, no services. The host references `MintPlayer.Spark.Commerce` and calls `spark.AddCommerce()`.

| Type | Purpose |
|---|---|
| `SparkCommerceOptions` (`Spark:Commerce`) | The whole configuration tree: merchant mode, channels (allocation, hold bounds, payment window, sign-in, waiting room, refunds, fees, resale, deceptive measures, country rules, required legal documents), basket (D40), sellers (D21, GA5, GA6), retention (§4.15), pricing (GA3), legal (GA10) |
| `AllocationMode`, `MerchantMode`, `SellerRegistrationMode`, `WaitingRoomMode`, `BasketLineFailurePolicy`, `PriceBasis`, `FeeKind`, `FeeScope`, `RefundPolicyKind`, `TraderStatus`, `KycStatus`, `CountryKey`, `CountryRuleEffect` | Policy enums |
| `DurationBounds`, `IntBounds` | Seller choices clamped into the channel's bounds when read (D2) |
| `ISellableItem` | What a basket line sells: channel, seller (`OrganizationId`), price, VAT basis |
| `IFinalPriceCalculator`, `FinalPriceRequest`, `FinalPrice`, `PriceComponent` | `FinalPrice` defined once (round 5, §4.4) |
| `ISparkPriceHistory`, `PriceHistoryEntry` | Append-only price history and the Omnibus prior price (GA3) |
| `ISparkSellers`, `SellerStanding`, `SellerGate` | Who may sell and receive payouts (D21, GA5, GA6) |
| `ISellerKycSource` | The KYC gate hook a payments adapter implements |
| `ICommercePaymentCapabilities` | Split capability (D40) and consent needs (GA9) a payments adapter declares |
| `ICountryKeyedRuleSource`, `CountryKeyedRule` | Country-keyed rules other packages declare for the geo-blocking check (GA11) |
| `LegalDocumentKinds`, `LegalDocumentRef`, `ISparkLegalDocuments` | Versioned legal documents and acceptance (GA2) |
| `ICommerceAudit`, `CommerceAuditEntry`, `CommerceAuditActions` | The money audit (default 8) |
| `CommerceDocumentTypes`, `RetentionFloor`, `IRetentionFloorSource`, `ICommerceRetention` | Retention per document type with legal floors (§4.15) |

`Money` (X4) and `TranslatedString` (X17) are not redeclared: they come with `MintPlayer.Spark.Abstractions`
(assembly `MintPlayer.Spark.Model`, namespace `MintPlayer.Spark.Abstractions`), which this package references, so
`using MintPlayer.Spark.Abstractions;` is all a commerce package needs.

See `docs/guide-commerce.md`.
