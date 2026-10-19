# MintPlayer.Spark.Commerce.Shipping.Test

The in-process test carrier of the MintPlayer.Spark commerce suite, for E2E tests and demos.

```csharp
spark.AddCommerceShipping().UseTestCarrier();
```

- Every capability: PDF and ZPL labels, multi-parcel, return labels, service points and lockers, customs documents,
  dangerous-goods marks, voids, webhooks and pull tracking.
- Services: `test-standard`, `test-dg` (lithium in or with equipment, limited quantities), `test-point`,
  `test-letter` (CN22/CN23 by value), `test-express` (commercial invoice), `test-return`.
- `TestCarrierSimulator.AdvanceAsync(trackingNumber, status)` scans a parcel and sends a signed webhook through the
  real route `/spark/webhooks/shipping-test` (optionally twice). Over HTTP:
  `POST /spark/commerce/shipping/test/simulate` (form: `trackingNumber`, `status`, `duplicate`).
- Refused in Production unless `Spark:Commerce:Shipping:Test:AllowInProduction` is set.
