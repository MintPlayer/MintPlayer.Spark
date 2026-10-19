# MintPlayer.Spark.Commerce.Shipping.PostNL

PostNL for the MintPlayer.Spark commerce suite, written against PostNL's published OpenAPI files
(`https://api.postnl.nl/shipment/*/swagger.json`).

```csharp
spark.AddCommerceShipping().UsePostNL();
```

```jsonc
// user-secrets
"Spark": { "Commerce": { "Shipping": { "PostNL": {
  "ApiKey": "…", "CustomerCode": "DEVC", "CustomerNumber": "11223344", "CollectionLocation": "123456",
  "Accounts": { "seller-1": { "ApiKey": "…", "CustomerCode": "…", "CustomerNumber": "…" } },
  "ApiBaseUrl": "https://api-sandbox.postnl.nl/",   // the sandbox; production is the default
  "Services": { "3085": { "AcceptedDangerousGoods": ["LithiumIonInEquipment"] } }   // only under your ADR contract (GC9)
} } } }
```

- **Labels:** `POST /shipment/v2_2/label?confirm=true`, PDF (`GraphicFile|PDF`) or ZPL. Several parcels are one
  request with a type-03 `Groups` entry each, after fetching barcodes from `GET /shipment/v1_1/barcode` (GB5).
- **Customs** (`Customs.Content` with `HSTariffNr` and `CountryOfOrigin`) only for non-EU shipments; the CN23 or
  commercial invoice comes back as an extra label (GB6). EUR or USD only.
- **Locations:** `GET /shipment/v2_1/locations/nearest` (NL and BE); lockers by `DeliveryOptions` `PA`. Delivery to a
  location sends its address as address type 09.
- **Tracking** is polled (`GET /shipment/v2/status/barcode/{barcode}`); status times are Dutch wall-clock times,
  converted through the core (`SparkTimeZones`).
- **No void:** the Labelling API has none; an unused label is not charged until scanned (confirm with the contract).

**Not confirmed (spike S13):**
- Products: 3085, 4946, 3285 and 4945 are on PostNL's pages or examples; **3533** (pick-up point) comes from
  integrators' documentation; 4945/4944 may have become 4909/4907. Override in `Services`.
- The ZPL printer type string (`ZplPrinterType`), and address type 04 for the consumer of a return label.
- PostNL's announced "Tracking Webhook" has no public specification; this adapter polls.
- Dangerous goods: `HazardousMaterial` needs substance codes Shipping does not hold; the reference gets the
  `ADR/LQ` prefix the products page asks for.

Tests use fixtures derived from PostNL's OpenAPI files (`tests/MintPlayer.Spark.Commerce.Tests/Shipping/Fixtures/PostNL`).
