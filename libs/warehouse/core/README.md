# MintPlayer.Spark.Warehouse

The core of the MintPlayer.Spark warehouse (alias `warehouse`, `docs/warehouse_picking_PRD.md` §4.5, WM3).

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();
    spark.AddReplication();
    spark.AddWarehouse();   // Spark:Warehouse; adds Organizations
});
```

- **Sites, zones, locations**: locations are separate documents (`{siteId}/locations/{code}`) with a materialized path,
  walk sequence, check digits and geometry; deactivated, never deleted once they have ledger history.
- **Owner links** (Q-W23): (shop module, owner id) → owning organization. They also filter replication: a shop receives
  status, shipments, stock levels and return receipts only for its linked owner ids. An owner id can be linked to one
  module only.
- **Items**: (owner, SKU) unique through a compare-exchange key; pre-items linked later without moving stock; the shops'
  `catalog-items` replicas create or link items and register their GTINs.
- **Barcodes**: registry ids `barcodes/{code}/…` (prefix loads), pipeline GS1 → own codes → registry
  (`IBarcodeResolutionStep`), several matches → the user chooses, unknown → link-to-item.
- **Stock engine** (`IWarehouseStock`): balances per (location, item, lot), an append-only ledger keyed by op id,
  stock levels in the same session (WI1–WI6), optimistic concurrency with retry, or cluster-wide transactions
  (`Spark:Warehouse:Stock:ClusterWide`).
- **Recall** (lot status Recalled) and an owner-scoped, audited recall trace with CSV export.
- **Replenishment** min/max on fixed pick faces (daily sweep + short picks), **tasks** with leases and op ids,
  **productivity events**.
- **Indexes**: five declared maps (`[SparkIndexMap]`): `Warehouse_Stock`, `Warehouse_LedgerTotals`,
  `Warehouse_Work`, `Warehouse_Catalog`, `Warehouse_Documents` (maps contributed by Inbound, Outbound, Packing).

Bind the slots in the application's `security.json`: `"bindings": { "warehouse:staff": [ … ], "warehouse:supervisors": [ … ] }`,
the library grants those slots `AllOrganizations/{Type}` on its owner-bearing types, so the operator's staff read every owner's rows (Organizations' cross-organization right).

High-churn stock levels flush as a replication dirty set every 2 s (`AddWarehouse` registers `FulfilmentStockLevels`);
the host can add more collections in `Spark:Replication:DirtySetCollections`.
