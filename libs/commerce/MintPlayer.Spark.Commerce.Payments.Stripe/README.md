# MintPlayer.Spark.Commerce.Payments.Stripe

Stripe for the MintPlayer.Spark commerce suite, written against Stripe's documented API.

```csharp
spark.AddStripePayments();
```

```jsonc
// user-secrets, never appsettings in source control
"Spark": { "Commerce": { "Payments": { "Stripe": {
  "SecretKey": "sk_test_…",
  "WebhookSecret": "whsec_…",         // endpoint /spark/webhooks/payments-stripe
  "ConnectWebhookSecret": "whsec_…",  // Connect endpoint /spark/webhooks/payments-stripe-connect
  "ApiVersion": "…",                  // pin it in production
  "ConnectAccountType": "standard",
  "SeparateChargesAndTransfers": false // D40/D41 split; platform becomes merchant of record; needs Basket:AcknowledgeSplitMerchantOfRecord
} } } }
```

- **Held flows:** a Checkout Session with `expires_at` set to the hold's PaymentPending deadline (clamped to Stripe's
  30 min – 24 h) and expired actively when the hold dies. Delayed methods (SEPA Direct Debit, bank transfer) are never
  offered in held channels.
- **Webhooks:** signed (`Stripe-Signature`, 5-minute tolerance), deduplicated on the event id. Subscribe the endpoint to
  `checkout.session.*`, `payment_intent.succeeded`, `payment_intent.amount_capturable_updated`,
  `payment_intent.canceled`, `payment_intent.processing`, `charge.succeeded`, `charge.captured`, `refund.*`,
  `charge.refund.updated`, `charge.dispute.*`; the Connect endpoint additionally `account.updated`.
- **Connect:** connected accounts and Account Links; direct charges with `application_fee_amount`, so refunds and
  disputes debit the seller's own balance.
- **Disputes:** evidence submitted by API (`POST /v1/disputes/{id}`, `submit=true`).
- **Settlements:** paid payouts with their balance transactions.

Tests use fixtures derived from Stripe's public documentation (`tests/MintPlayer.Spark.Commerce.Tests/Payments/Fixtures/Stripe`),
to be replaced by sandbox recordings once the development account exists.
