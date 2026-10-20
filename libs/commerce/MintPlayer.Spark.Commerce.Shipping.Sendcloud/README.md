# MintPlayer.Spark.Commerce.Shipping.Sendcloud

Sendcloud for the MintPlayer.Spark commerce suite, written against Sendcloud's documented **API v3** (sendcloud.dev).
v2 parcel creation is closed to accounts created on or after 13 April 2026, so this adapter uses v3 only.

```csharp
spark.AddCommerceShipping().UseSendcloud();
```

```jsonc
// user-secrets, never appsettings in source control
"Spark": { "Commerce": { "Shipping": { "Sendcloud": {
  "PublicKey": "…", "SecretKey": "…",              // the default account; the secret key also signs webhooks
  "WebhookSignatureKey": "…",                      // only when the integration shows a separate one
  "Accounts": { "seller-1": { "PublicKey": "…", "SecretKey": "…" } },   // per owner (warehouse WD8)
  "Services": {                                    // the account's shipping_option_codes (POST /api/v3/shipping-options)
    "postnl:standard":     { "Name": "PostNL Standard", "Countries": ["NL", "BE"], "MaxWeightGrams": 23000 },
    "postnl:servicepoint": { "Name": "PostNL service point", "Countries": ["NL"], "ServicePoint": true },
    "postnl:return":       { "Name": "PostNL return", "Return": true, "FromCountries": ["NL"] },
    "dhl:express":         { "AcceptedDangerousGoods": ["LithiumIonInEquipment"] }   // only what your contract allows (GC9)
  },
  "ContractIds": { "postnl:standard": 517 },       // when the account has several contracts for a carrier
  "ServicePointCarriers": ["postnl", "bpost"],     // empty: the integration's enabled carriers
  "SenderAddressId": 12345,                        // optional: from_address by id instead of the ship-from address
  "PaperSize": "A6", "ZplDpi": 203
} } } }
```

- **Shipments:** `POST /api/v3/shipments/announce` (synchronous: the carrier's answer in the same call), one shipment
  with up to 15 parcels, each with its own label and tracking (GB5). `ship_with` names the `shipping_option_code`
  (and `contract_id` when configured); our booking key is `external_reference_id` and the order is `order_number`;
  service points go in `to_service_point.id`; customs in `customs_information` (`invoice_number`, `export_reason`) and
  `parcel_items` (HS code, origin, per-item weight and price) on the first parcel (GB6).
- **Labels and customs documents:** `GET /api/v3/parcels/{id}/documents/label|customs-declaration` in the asked format
  (`application/pdf`, `application/zpl`, `image/png`; `label_details` asks the carrier for the same).
- **Idempotency:** Sendcloud keeps `external_reference_id` unique per account: a repeated announce answers **409 with
  the existing shipment**, read as the booking. A booking whose answer was lost is found with
  `GET /api/v3/shipments?external_reference_id=` (`IShippingBookingLookup`); only a shipment whose
  `external_reference_id` equals ours exactly counts, since the reference does not say the filter matches exactly. v3
  documents no `Idempotency-Key` header: the external reference is the idempotency key. A failed announcement (201 with
  `ANNOUNCEMENT_FAILED` / `parcel_announcement_error`) is a refusal (422): Shipping retries under a fresh reference.
- **Returns:** `POST /api/v3/returns/announce-synchronously` from the buyer to the seller (`external_reference`,
  `export_reason: returned_goods`), then `GET /api/v3/returns/{id}` for tracking and the parcel's label document. One
  parcel per return. The Returns API cannot be searched by reference, so a lost return answer waits for staff.
- **Void:** `POST /api/v3/shipments/{id}/cancel` cancels the **whole shipment** (200 cancelled, 202 queued count as
  voided; 404/409 cannot). Shipping therefore voids all its parcels together (`VoidsWholeShipment`). Returns are not
  voided through the API.
- **Tracking:** the signed `parcel_status_changed` webhook (`POST /spark/webhooks/shipping-sendcloud`, verified by
  `Sendcloud-Signature`, hex HMAC-SHA256 of the raw body, against every configured secret and webhook key; set this URL
  in the integration's settings), and `GET /api/v3/parcels/tracking/{tracking_number}` for a pull.
- **Service points:** `GET /api/v3/service-points` (`country_code`, `carrier_code` or `use_integration_carriers`,
  `address_postal_code`/`address_street`/`address_house_number`, `general_shop_type`); lockers are
  `general_shop_type: locker`, expired points are skipped.
- **Credentials** go only to the configured API host; a link on another host is refused.

**Not confirmed (spike S13, v3):**
- Whether `external_reference_id` accepts `/` (our keys look like `Shipments/orders/1`); the docs state only uniqueness.
- The v3 tracking phases beyond the documented examples (`accepted`, `in_transit`, `delivered`); others are classified
  by name and description.
- The returns `ship_with` shape (`shipping_option_code` at the top of `ship_with`, `contract`), as the returns page
  states it, unlike the shipments page's `properties`.
- The webhook payload is documented as "the parcel as retrieved"; this adapter reads the v2-style example
  (`action`, `timestamp`, `parcel.status.id`) and also accepts a v3 `status.code`. The timestamp unit is not stated
  (read as milliseconds above 10^11). v3 also has event subscriptions (`parcels.event.created`, `data.event`,
  `data.parcel.source_id`), but their reference page gives no payload example or signature header, so this adapter
  does not parse them yet.
- `price.value` of customs lines is sent as a decimal string, as the examples show; the schema's type is not stated.
- Dangerous goods: `parcel_items[].dangerous_goods` needs IATA/ADR fields Shipping does not hold, so none are sent.

Tests use fixtures derived from Sendcloud's public v3 documentation (`tests/MintPlayer.Spark.Commerce.Tests/Shipping/Fixtures/Sendcloud`).
