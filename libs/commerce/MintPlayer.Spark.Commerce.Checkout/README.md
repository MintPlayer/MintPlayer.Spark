# MintPlayer.Spark.Commerce.Checkout

Checkout for the MintPlayer.Spark commerce suite (alias `commerce-checkout`, Angular entry
`@mintplayer/ng-spark/commerce/checkout`): baskets for guests and accounts, the order state machine for the three
allocation modes, payment orchestration, the "order with obligation to pay" step, disputes, back-office orders, age
attestation, abandoned-basket reminders, donations and insurance.

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();
    spark.AddTestPayments();          // or AddMolliePayments() / AddStripePayments()
    spark.AddCommerceCheckout();      // Spark:Commerce:Checkout; adds Inventory and Payments
});
```

What you sell reaches checkout through an `ICheckoutCapacitySource` (Shop and Ticketing register theirs). Promotions,
Invoicing, Shipping, Fulfilment and Ticketing plug in through `ICheckoutPricingStep`, `IOrderEventRecipient` and
`ICheckoutCapacitySource`; checkout references none of them. The waiting room implements `ICheckoutAdmissionGate`.

See [docs/guide-commerce-checkout.md](../../../docs/guide-commerce-checkout.md).

After the sale (M9): refunds and cancellations within the seller's policy, the unconditional withdrawal function
(GA1), legal-guarantee claims and return reasons (GA8), and resumable mass refunds. Credit notes and ticket revocation
plug in through the `RefundIssued` and `TicketsRevoked` order events. See
[docs/guide-commerce-refunds.md](../../../docs/guide-commerce-refunds.md).
