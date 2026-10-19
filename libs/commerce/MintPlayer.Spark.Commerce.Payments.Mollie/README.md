# MintPlayer.Spark.Commerce.Payments.Mollie

Mollie for the MintPlayer.Spark commerce suite, written against Mollie's documented v2 API.

```csharp
spark.AddMolliePayments();
```

```jsonc
// user-secrets, never appsettings in source control
"Spark": { "Commerce": { "Payments": { "Mollie": {
  "ApiKey": "test_…",              // the platform's own payments
  "OrganizationToken": "access_…", // the platform's own settlements (the Settlements API takes no API key)
  "ClientId": "app_…",             // Mollie Connect (ConnectedSellers mode)
  "ClientSecret": "…",
  "SplitPayments": false            // D40 routing; only once Mollie enabled it on the account
} } } }
```

- **Webhooks:** `POST /spark/webhooks/payments-mollie`, fetch-by-id. Mollie signs nothing and sends only `id=tr_…`;
  the payment is fetched with the credentials it was created with. Never deduplicated on the id.
- **Connect:** OAuth authorization code; the redirect URI to register with the Mollie app is
  `{public base}/spark/commerce/payments/connect/callback`. Access tokens are renewed with the refresh token and stored
  encrypted (Data Protection). Verification is Mollie's onboarding status, polled every 15 minutes.
- **Chargebacks** are disputes without a deadline and without an evidence API: the evidence pack is a download for
  Mollie's dashboard. One not reversed after `Spark:Commerce:Payments:Disputes:ChargebackFinalAfter` is closed as lost.
- **Settlements:** paid-out settlements with their payments, refunds, chargebacks and costs.

Tests use fixtures derived from Mollie's public documentation (`tests/MintPlayer.Spark.Commerce.Tests/Payments/Fixtures/Mollie`),
to be replaced by sandbox recordings once the development account exists.
