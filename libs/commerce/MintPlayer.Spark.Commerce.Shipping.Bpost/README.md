# MintPlayer.Spark.Commerce.Shipping.Bpost

bpost for the MintPlayer.Spark commerce suite. bpost publishes no public API reference: this adapter follows the
shapes of the shipping-manager library bpost's own e-commerce plugins use (`Antidot-be/bpost-api-library`), and its
tests use that library's examples, marked **SDK-derived** (D37). Confirm with bpost before going live.

```csharp
spark.AddCommerceShipping().UseBpost();
```

```jsonc
// user-secrets
"Spark": { "Commerce": { "Shipping": { "Bpost": {
  "AccountId": "…", "PassPhrase": "…",
  "Accounts": { "seller-1": { "AccountId": "…", "PassPhrase": "…" } },
  "LocatorPartner": "…",                  // the pick-up point locator's partner id, from bpost
  "LabelSize": "A6",
  "Services": { "bpack 24h Pro": { "AcceptedDangerousGoods": [] } }
} } } }
```

- **Orders:** `POST {base}/{accountId}/orders` (`application/vnd.bpost.shm-order-v5+XML`), one `box` per parcel:
  `atHome` (`bpack 24h Pro`), `atBpost` (`bpack@bpost`, pick-up point), `at247` (`bpack 24/7`, locker), or an
  international box with `customsInfo` and `parcelContents` (HS code, origin) (GB5, GB6).
- **Labels:** `POST …/orders/{reference}/labels/A6` (PDF). One PDF may hold several boxes; each box's parcel keeps it.
- **Void:** the order's status is set to `CANCELLED` (all its parcels).
- **Tracking** is polled from the fetched order's box statuses (`ANNOUNCED`, `IN_TRANSIT`, `AWAITING_PICKUP`,
  `DELIVERED`, `BACK_TO_SENDER`); bpost publishes no tracking webhook or public tracking API.
- **Pick-up points:** `pudo.bpost.be/Locator?Function=search` (Belgium); type 4 is a bpack 24/7 locker.

**Not confirmed (spike S13):** the base URL (`shm-rest.bpost.cloud` vs `api.bpost.be`), the value unit of
`parcelValue`/`valueOfItem` (read as eurocents), the `at247` fields, how `bpack Easy Retour` reads its addresses,
product weight limits, and anything about dangerous goods (none documented). A real tracking source needs bpost's
separate tracking service.
