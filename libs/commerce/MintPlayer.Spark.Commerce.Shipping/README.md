# MintPlayer.Spark.Commerce.Shipping

Shipping for the MintPlayer.Spark commerce suite (alias `commerce-shipping`, commerce PRD §4.9). It has **no Checkout
dependency**: a shop host and the warehouse pack station register it the same way.

```csharp
spark.AddMessaging();                 // carrier webhooks run from the spark-webhooks queue
spark.AddCommerceShipping()
    .UseSendcloud()                   // or UseBpost(), UsePostNL(), UseTestCarrier(); several at once
    .UseReferenceDataHolidays();      // optional: skip public holidays (GC11)
```

- **Methods and zones** from `Spark:Commerce:Shipping`: a flat rate per zone, a free-shipping threshold per zone, and
  pickup. `IShippingRateProvider` adds rate sources (`[SparkDiscoverable]`).
- **Carrier capability catalog:** every service states its destinations, weights, service-point delivery, customs
  style and accepted dangerous-goods classes. A service that cannot take a parcel is never offered and never booked.
- **Labels** (PDF or ZPL with its dpi) are attachments of the `Shipment` document; **multi-parcel** shipments,
  **return labels** and **voids** (GB5).
- **Booking idempotency:** each carrier call is recorded as a pending booking before it is made; a booking whose
  answer was lost is looked up at the carrier by its reference (`IShippingBookingLookup`) before it is booked again,
  by the next attempt or the `Shipping.BookingReconcile` job. Carriers without a lookup wait for staff (*Release booking*).
- **Service points and lockers** (GB5): `GET /spark/commerce/shipping/service-points`, and ng-spark's
  `<spark-service-point-picker>` (`@mintplayer/ng-spark/commerce/shipping`).
- **Customs** (GB6): CN22/CN23 or a commercial invoice through the adapter for shipments leaving the EU customs
  territory (and to the Canary Islands, Åland, Mount Athos); DAP or DDP per destination zone.
- **Dangerous goods** (GC9): lithium-battery and ADR classes restrict services and add label marks.
- **Cut-offs and delivery promises** (GC11) skip weekends and public holidays.
- **Tracking:** webhooks through `MintPlayer.Spark.Webhooks` (`/spark/webhooks/shipping-{carrier}`), or a poll for
  carriers without them; deduplicated on the event id and ordered by event time. `IShipmentEventRecipient` hears it.
- **Rights:** the `commerce-shipping:staff` slot, which the application binds (`"bindings": { "commerce-shipping:staff": ["Shipping"] }`).

See `docs/guide-commerce-shipping.md`.
