# Commerce shipping — what you write

`MintPlayer.Spark.Commerce.Shipping` (alias `commerce-shipping`) and its carrier adapters `.Sendcloud`, `.Bpost`,
`.PostNL` and `.Test` (commerce PRD §4.9, milestone M11; D12, D14). Shipping has **no Checkout dependency**: the shop's
checkout step (Shop, M12) and the warehouse pack station (warehouse §4.10–4.12) both call `ISparkShipping`.

## 1. Register

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();                 // tracking webhooks are handled from the spark-webhooks queue
    spark.AddCommerceShipping()
        .UseSendcloud()                   // UseBpost(), UsePostNL(), UseTestCarrier(): the host picks (K7)
        .UseReferenceDataHolidays();      // optional (GC11)
});
```

Bind the staff slot in the application's `security.json`:

```json
"bindings": { "commerce-shipping:staff": ["Shipping"] }
```

Members see the `Shipments` query, download labels and customs documents, and run *Void labels*, *Return label*,
*Add parcel* and *Release booking* (section 5). A marketplace narrows rows with an `IShipmentRowFilter` (a seller's staff see their own `Owner`'s).

## 2. Configure methods, zones, dispatch

```jsonc
"Spark": { "Commerce": { "Shipping": {
  "Carrier": "sendcloud",                       // the default; required with several adapters
  "Currency": "EUR",
  "Sender":        { "Name": "Shop", "Street": "Rue de la Loi", "HouseNumber": "16", "PostalCode": "1000", "City": "Brussels", "Country": "BE" },
  "ReturnAddress": { /* where return labels go; Sender when unset */ },
  "Zones": {
    "benelux": { "Countries": ["BE", "NL", "LU"] },
    "uk":      { "Countries": ["GB"], "Incoterm": "Ddp" },   // GB6: who pays duties, shown at checkout
    "world":   { "Countries": ["*"] }
  },
  "Methods": {
    "standard": { "Carrier": "sendcloud", "Service": "postnl:standard", "Rates": { "benelux": 4.95, "world": 19.95 }, "FreeFrom": { "benelux": 50 }, "TransitDays": { "Min": 1, "Max": 2 } },
    "locker":   { "Carrier": "bpost", "Service": "bpack 24/7", "Rates": { "benelux": 3.95 } },
    "pickup":   { "Kind": "Pickup", "Rates": { "benelux": 0 }, "PickupLocation": "Rue de la Loi 16, Brussels" }
  },
  "Dispatch": { "TimeZone": "Europe/Brussels", "CutOff": "15:00", "HolidayCountry": "BE", "WorkingDays": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"] },
  "Customs": { "Cn22MaxValue": 300 },
  "LabelFormat": "Pdf",                         // or Zpl for thermal printers
  "TrackingPollSchedule": "*/30 * * * *"
} } }
```

Startup is refused for: no carrier, an ambiguous or unknown default carrier, a method naming an unknown carrier,
service or zone, a malformed zone country, an unknown currency or dispatch time zone.

Zones are keyed on the **delivery** country, which the Geo-blocking Regulation allows (GA11): Shipping never sees the
buyer's nationality or residence.

## 3. Rates at checkout

```csharp
var rates = await shipping.GetRatesAsync(new ShippingRateRequest
{
    ShipTo = address, Subtotal = basket.GoodsTotal, WeightGrams = basket.Weight,
    DangerousGoods = basket.Lines.SelectMany(l => l.DangerousGoods).ToList(),   // GC9
}, ct);
```

Each rate is checked against its carrier's catalog: destination, weight, dangerous goods (a service that does not
accept a class is **never offered**), service-point capability. A rate carries `Customs` and `Incoterm` (GB6),
`ServicePoint` (show the picker, GB5) and `Promise` (GC11). Add rate sources with `IShippingRateProvider`
(`[SparkDiscoverable]`). The storefront's anonymous `GET /spark/commerce/shipping/rates?country=…&subtotal=…` serves the
"free shipping from" teaser.

## 4. Service points and lockers (GB5)

`GET /spark/commerce/shipping/service-points?country=BE&postalCode=1000[&carrier=bpost&kind=parcelLocker]`
(anonymous, cached per query) and the Angular picker:

```html
<spark-service-point-picker [country]="address.country" [postalCode]="address.postalCode" carrier="bpost" [(selected)]="point" />
```

The chosen point goes to `CreateShipmentRequest.ServicePoint` and onto the label.

## 5. Book, add parcels, return, void

```csharp
var shipment = await shipping.CreateShipmentAsync(new CreateShipmentRequest
{
    Reference = order.Id!, Method = order.ShippingMethod, ShipTo = order.Address, ServicePoint = order.Point,
    Parcels = [new ParcelRequest(1200), new ParcelRequest(800)],             // GB5: one label per parcel
    CustomsItems = lines,                                                   // GB6, when customs apply
    InvoiceNumber = invoice.Number,
    DangerousGoods = dangerousGoods,                                        // GC9: marks go on the label
    IdempotencyKey = order.Id + "/outbound",                                // a retry books nothing twice
}, ct);
await shipping.AddParcelAsync(shipment.Id!, new ParcelRequest(900), userId, ct);              // shop-only multi-parcel
await shipping.CreateReturnLabelAsync(new ReturnLabelRequest { ShipmentId = shipment.Id!, Reason = "withdrawal" }, ct);
await shipping.VoidAsync(shipment.Id!, parcelNumber: null, userId, ct);                         // before handover only
```

Labels are attachments of the shipment (`label-{n}.pdf|zpl`, `customs.pdf`), downloaded from
`GET /spark/commerce/shipping/label?id=…&parcel=n` (staff). A multi-parcel shipment with a carrier that cannot book
several parcels at once is booked parcel by parcel; each parcel keeps its own tracking.

**Booking idempotency.** Every carrier call is first recorded on the shipment as a `PendingBooking` (the parcels it
covers and the reference the carrier sees: the shipment id, `/{n}` for a booking of part of it), and marked *in doubt*
before the call. When the answer is lost (a timeout, a crash before the labels are saved), nothing is booked blindly
again:

- the next attempt (the same `IdempotencyKey`), and the `Shipping.BookingReconcile` job (every 10 minutes, for
  bookings older than `BookingReconcileAfter`, default 10 minutes), first **look the booking up at the carrier** by
  that reference (`IShippingBookingLookup`: Sendcloud `GET /shipments?external_reference_id=`, bpost's order, the
  test carrier; Sendcloud v3 has no `Idempotency-Key` header, and a repeated announce with the same
  `external_reference_id` answers 409 with the shipment it holds, so even a blind retry books nothing twice). Found:
  its labels are stored as if the answer had arrived. Not found (and old enough not to be in
  flight): it is booked again. The job never books.
- a carrier that cannot be asked (PostNL; Sendcloud returns) leaves it in doubt: the attempt throws
  `ShipmentBookingInDoubtException`, and staff check the carrier's panel and run *Release booking*
  (`ReleasePendingBookingAsync`), after which the next attempt books.
- a refusal (a 4xx, or Sendcloud's `ANNOUNCEMENT_FAILED`) booked nothing: the next attempt books under a fresh
  reference (`{reference}-r{n}`), since Sendcloud keeps a refused shipment's reference.

An added parcel follows the same path; a refused one is removed again. Carriers that cancel whole bookings
(`VoidsWholeShipment`: Sendcloud, bpost) void every live parcel of a booking with one call and refuse to void one
parcel of several.

**Customs (GB6).** Customs apply when either end is outside the EU customs territory (Regulation (EU) No 952/2013,
Art. 4) or outside the VAT territory (Canary Islands, Åland, Mount Athos: a declaration for fiscal purposes). Art. 4
was checked against EUR-Lex 32013R0952 on 2026-10-20: **Monaco** (Art. 4(2)(a)) and the UK Sovereign Base Areas in
Cyprus are part of the customs territory but missing from the default list; add `MC` to `Customs:CustomsTerritory` if
you ship there. The postal-code exceptions in `Customs:TerritoryExceptions` were not checked per country. Every line
needs an HS code (6, 8 or 10 digits), a country of origin, a value and a weight (Shop stores them per product or
variant, M12); commercial goods need an invoice number. The document follows the service: letter post CN22 up to
`Cn22MaxValue` (the UPU limit is 300 SDR; the default 300 EUR stays below it) and CN23 above, postal parcels CN23,
express a commercial invoice.

**Dangerous goods (GC9).** Classes: lithium-ion/metal batteries in, with or without equipment (UN3481/UN3480,
UN3091/UN3090), ADR limited and excepted quantities, other. Adapters ship **no** accepted classes; the host states what
its carrier contract allows:

```json
"Spark": { "Commerce": { "Shipping": { "PostNL": { "Services": { "3085": { "AcceptedDangerousGoods": ["LithiumIonInEquipment"] } } } } } }
```

## 6. Tracking

Carriers with webhooks post to `/spark/webhooks/shipping-{carrier}` (verified by the adapter's scheme, queued,
retried). Others are polled by `Shipping.TrackingPoll`. Events are deduplicated on their id and ordered by event time,
so late and duplicate deliveries are harmless. `IShipmentEventRecipient` (`[SparkDiscoverable]`) hears
`OnShipmentLabelledAsync`, `OnReturnLabelCreatedAsync` (mail the return label to the buyer) and
`OnTrackingChangedAsync`; its `ProofOfDispatch` is set on a return's first carrier scan, which starts a withdrawal's
refund "on receipt or proof of dispatch, whichever is earlier" (GA1, Directive 2011/83/EU Art. 13(3)).

## 7. Holidays (GC11)

`UseReferenceDataHolidays()` reads the reference data's `PublicHolidays`, synced into the application's database by
the reference-data client. Cut-offs skip `Dispatch:HolidayCountry`'s holidays; delivery promises also skip the
destination's (`Dispatch:SkipDestinationHolidays`). A host can register its own `IShippingHolidayCalendar`.
Wall-clock conversions go through the core (`SparkTimeZones`, K10).

## 8. Carriers

| Adapter | Labels | Tracking | Service points | Returns | Multi-parcel | Customs |
|---|---|---|---|---|---|---|
| Sendcloud (API v3) | PDF / ZPL / PNG | webhooks (`Sendcloud-Signature`) + pull | v3 service points | Returns API | one shipment, up to 15 parcels | `parcel_items` + customs declaration |
| bpost | PDF | poll (order box status) | pick-up point locator | `bpack Easy Retour` | one box per parcel | `customsInfo` |
| PostNL | PDF / ZPL | poll (Shippingstatus) | Locations API | product 3285 | `Groups` (multi-collo) | `Customs` + CN23 label |

Voids: Sendcloud and bpost cancel a whole booking (`VoidsWholeShipment`: all its parcels together, never one of several), PostNL has none. Booking lookups (section 5): Sendcloud by `external_reference_id` (outbound only), bpost by order reference, the test carrier; PostNL has none.
| Test | PDF / ZPL | webhooks + pull | built in | `test-return` | yes | yes |

Each adapter's README lists its settings and what its fixtures could not confirm. Tests use fixtures derived from the
carriers' public documentation (`tests/MintPlayer.Spark.Commerce.Tests/Shipping/Fixtures`), marked as such (D37).
