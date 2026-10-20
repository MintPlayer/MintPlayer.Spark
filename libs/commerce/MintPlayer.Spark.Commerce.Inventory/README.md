# MintPlayer.Spark.Commerce.Inventory

The inventory engine of the Spark commerce suite (alias `commerce-inventory`; `docs/commerce_PRD.md` §4.3;
guide: `docs/guide-commerce-inventory.md`).

```csharp
builder.Services.AddSpark(builder.Configuration, spark => spark.AddCommerceInventory());
```

`ISparkInventory` holds, adjusts, extends, converts, releases and returns **claims** over:

- **Quotas** (`InventoryQuota`), split over shard documents (`{quotaId}/shards/{k}`) so concurrent claims don't
  contend for one document. Sellable **items** map to one or more quotas with a **multiplier** (bundles, D15).
- **Numbered units** (seats): X12 compare-exchange claims `commerce/units/{unitId}`, taken over after expiry + grace.
  **Chain fill** seats a group in the smallest hole before the cursor, else in the next run (D25); **pick your seat**
  is checked against the **single-empty-seat rule** before it claims.
- **Per-buyer limits**: compare-exchange counters `commerce/limits/{scope}/{kind}/{hash}`, one per linked identifier
  (`InventoryLimitKeys.For`: account, visitor, normalised e-mail, address, card fingerprint).

Every operation is one cluster-wide transaction, idempotent by claim id (or return id), retried on a conflict:

| | |
|---|---|
| I1 | `sold + live holds ≤ capacity` for every shard, at every instant |
| I2 | a claim (or an adjustment) takes everything it asks for, or nothing |
| I3 | the sweeper (cron, every 5 s) is housekeeping only; expiry is lazy |
| I4 | every return path (expiry, payment failure, refund, cancellation, resale, late payment, withdrawal, refused line, lost dispute) returns each counter exactly once; `ReturnStock: false` keeps shipped units sold until `RestockAsync` |
| I5 | a hold stays live until `ExpiresAt + Grace` |

`ExchangeAsync` returns, claims and sells in one transaction (seat change, upgrade); `GetItemMappingAsync` is the
public read of an item's quota mapping.

Live availability is published per quota on the public topic `commerce-inventory:{quotaId}` after every commit, and
every `IInventoryCommitted` is told which quotas and units the commit touched. The sweeper's index is declared
(`InventoryIndexes`, `[SparkIndexMap]`, X19) and emitted by the application's generator.

The engine's documents are written only by the engine (S18): they are no persistent-object types (startup refuses
one that maps them), their collections have revisions turned off at startup, and nothing patches them.
