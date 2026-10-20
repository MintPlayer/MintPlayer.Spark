# MintPlayer.Spark.Commerce.Payments

Payments for the MintPlayer.Spark commerce suite (`docs/commerce_PRD.md` §4.5; guide:
[docs/guide-commerce-payments.md](../../../docs/guide-commerce-payments.md)). Alias `commerce-payments`; Angular entry
point `@mintplayer/ng-spark/commerce/payments`.

A host adds one provider package, which adds this one:

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();           // webhooks are handled from the spark-webhooks queue
    spark.AddMolliePayments();      // or AddStripePayments(), AddTestPayments()
});
```

What it gives:

- **`ISparkPayments`**: create (idempotent per order reference and attempt), get, cancel, capture, void, refund
  (idempotent per refund reference; an insufficient seller balance is a recorded failure, never an exception).
- **Method catalog** per provider: max expiry, authorization, fingerprint timing, split capability. `GetOfferableMethods`
  applies D3's cap for held channels; it takes no country (GA11).
- **Merchant modes** (`Spark:Commerce:Merchant`): `Platform`, or `ConnectedSellers` with Connect onboarding
  (`POST /spark/commerce/payments/connect/start`, callback, status), KYC fed into Commerce's `ISellerKycSource`, and
  application fees (`Spark:Commerce:Payments:PlatformFee`).
- **Webhooks** through MintPlayer.Spark.Webhooks: `/spark/webhooks/payments-{provider}`; events recorded idempotently
  and handed to every `IPaymentEventRecipient` (Checkout, Tickets).
- **Disputes** (GB1): `PaymentDispute` rows, seller notification (`IPaymentNotifier`, or a MailManager mail), an
  evidence pack from every `IDisputeEvidenceContributor` (submitted by API where the provider takes it, else a
  download), the Connect debit in the fee ledger.
- **Settlements** (GB4): each adapter's `ISettlementSource` is imported hourly and matched; mismatches and failed
  refunds land in the `ReconciliationQueue` for the `finance` role; the `PlatformFeeLedger` records every application
  fee, fee refund and dispute debit.

Rows are visible to the seller's Owners, Admins and `finance` members, and to those of
`Spark:Commerce:Payments:PlatformOrganizationId` for every seller.
