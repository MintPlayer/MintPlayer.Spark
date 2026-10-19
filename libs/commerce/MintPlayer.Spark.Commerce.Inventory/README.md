# MintPlayer.Spark.Commerce.Inventory

The inventory engine of the Spark commerce suite (`docs/commerce_PRD.md` §4.3).

- **Quotas** are split over shard documents (`{quotaId}/shards/{k}`) so concurrent claims don't contend for one document.
- **Numbered units** (seats) are compare-exchange values `inventory/units/{unitId}`.
- **Per-buyer limits** are compare-exchange values `inventory/limits/{key}`, one per linked identifier.
- **Claims** (`InventoryClaim`) record exactly what a basket or order holds.

Every operation (`TryClaimAsync`, `ExtendAsync`, `ConvertAsync`, `ReleaseAsync`) is one cluster-wide transaction, idempotent by claim id, and retried on a conflict from another shard. The invariants:

| | |
|---|---|
| I1 | `sold + live holds ≤ capacity` for every shard, at every instant |
| I2 | a claim takes everything it asks for, or nothing |
| I3 | the sweeper (`SweepAsync`) is housekeeping only; expiry is lazy |
| I4 | every release returns each counter exactly once; `returnStock: false` keeps shipped units sold |
| I5 | a hold stays live until `ExpiresAt + Grace` |
