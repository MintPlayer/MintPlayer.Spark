# MintPlayer.Spark.Commerce.Shipping.Sendcloud

Sendcloud for the MintPlayer.Spark commerce suite, written against Sendcloud's documented API (sendcloud.dev).

```csharp
spark.AddCommerceShipping().UseSendcloud();
```

```jsonc
// user-secrets, never appsettings in source control
"Spark": { "Commerce": { "Shipping": { "Sendcloud": {
  "PublicKey": "…", "SecretKey": "…",              // the default account; the secret key also signs webhooks
  "Accounts": { "seller-1": { "PublicKey": "…", "SecretKey": "…" } },   // per owner (warehouse WD8)
  "Services": {                                    // the account's shipping-method ids (GET /shipping_methods)
    "8":   { "Name": "PostNL Standard", "Countries": ["NL", "BE"], "MaxWeightGrams": 23000 },
    "512": { "Name": "PostNL service point", "Countries": ["NL"], "ServicePoint": true },
    "9":   { "Name": "PostNL return", "Return": true, "FromCountries": ["NL"] },
    "8020":{ "AcceptedDangerousGoods": ["LithiumIonInEquipment"] }   // only what your contract allows (GC9)
  },
  "ServicePointCarriers": ["postnl", "bpost"],
  "SenderAddressId": 12345
} } } }
```

- **Parcels:** every parcel of a shipment is one entry of a batch `POST /api/v2/parcels` with `request_label`, so each
  has its own label (A6 `label_printer` PDF by default) and tracking. Returns set `is_return`; service-point delivery
  `to_service_point`; customs `customs_information` + `parcel_items` (HS code, origin, per-item weight and value).
- **Void:** `POST /parcels/{id}/cancel` (200 or 202 count as voided).
- **Webhooks:** `POST /spark/webhooks/shipping-sendcloud`, verified by `Sendcloud-Signature` (hex HMAC-SHA256 of the
  raw body) against the default and every named account's secret key; set this URL in the integration's settings.
- **Service points:** `servicepoints.sendcloud.sc/api/v2/service-points`; lockers by `general_shop_type: locker`.
- **Credentials** go only to Sendcloud's configured hosts; a label URL on another host is refused.

**Not confirmed (spike S13):**
- **v2 is closed to accounts created on or after 13 April 2026**; those must use v3 (`/api/v3/shipments/announce`),
  which this adapter does not map yet.
- Most numeric status ids (only 11, 1000, 1002, 2000, 3–6 are in the docs; others are classified by their message),
  and the unit of the webhook `timestamp` (read as milliseconds above 10^11).
- The day order of `formatted_opening_times` (0 read as Monday).
- How v2 reads the addresses of a return parcel (the buyer's address is sent as the parcel's address).
- Dangerous goods: `parcel_items[].dangerous_goods` needs IATA/ADR fields Shipping does not hold, so none are sent.

Tests use fixtures derived from Sendcloud's public documentation (`tests/MintPlayer.Spark.Commerce.Tests/Shipping/Fixtures/Sendcloud`).
