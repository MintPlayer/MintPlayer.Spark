# MintPlayer.Spark.Commerce.Payments.Test

The in-process test payment provider of the MintPlayer.Spark commerce suite, for E2E tests and demos. No money moves.

```csharp
spark.AddTestPayments();   // refused in Production unless Spark:Commerce:Payments:Test:AllowInProduction
```

- A payment page at `/spark/commerce/payments/test/pay?id=…` with one button per outcome: pay, authorize, pending, fail,
  cancel, expire, pay with a late webhook, pay with a duplicated webhook.
- `TestPaymentSimulator` for tests: `SimulateAsync(id, outcome, delivery)`, `SetRefundBehaviour` (succeed, pending,
  insufficient balance, fail), `CompleteRefundAsync`, `OpenDisputeAsync` / `CloseDisputeAsync`,
  `SetAccountStatusAsync`, `DeliverLateAsync`.
- Webhooks take the real path: `/spark/webhooks/payments-test`, HMAC-signed with a per-process key, queued and
  deduplicated on the event id.
- Test Connect onboarding (instant, ending in `OnboardingOutcome`) and test settlements for reconciliation.
