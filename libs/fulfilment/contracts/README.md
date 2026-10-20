# MintPlayer.Spark.Fulfilment.Contracts

The contracts between a shop and a warehouse built on MintPlayer.Spark (`docs/warehouse_picking_PRD.md` §4.3, WD2):
state goes both ways, every fact has one owner, and no app writes the other's documents. Each contract is **one
`[Replicated]` class**: its owner stores it as an ordinary document, and the host that consumes it holds read-only
replicas of the same class, generated manifest, script and hash included (WD13, WD14).

| Contract | Class (collection) | Owner → consumer |
|---|---|---|
| `fulfilment-orders` | `FulfilmentOrder` (`FulfilmentOrders`) | shop → warehouse |
| `catalog-items` | `FulfilmentCatalogItem` (`FulfilmentCatalogItems`) | shop → warehouse |
| `expected-returns` | `FulfilmentExpectedReturn` (`FulfilmentExpectedReturns`) | shop → warehouse |
| `expected-receipts` | `FulfilmentExpectedReceipt` (`FulfilmentExpectedReceipts`) | shop or ERP → warehouse (Q-W9) |
| `fulfilment-status` | `FulfilmentOrderStatus` (`FulfilmentOrderStatuses`) | warehouse → shop |
| `shipments` | `FulfilmentShipment` (`FulfilmentShipments`) | warehouse → shop |
| `stock-levels` | `FulfilmentStockLevel` (`FulfilmentStockLevels`) | warehouse → shop |
| `return-receipts` | `FulfilmentReturnReceipt` (`FulfilmentReturnReceipts`) | warehouse → shop |

## Binding a host

The classes name no `SourceModule`, and the assembly is marked `[assembly: SparkReplicaContracts]`. A host
consumes exactly the contracts it binds to their owner modules; the rest are inert in that host (its own
documents, or unused):

```json
{
  "Spark": {
    "Replication": {
      "Sources": {
        "FulfilmentOrders": [ "Shop" ],
        "FulfilmentCatalogItems": [ "Shop" ],
        "FulfilmentExpectedReturns": [ "Shop" ]
      }
    }
  }
}
```

A warehouse serving several shops lists each: `"FulfilmentOrders": [ "ShopA", "ShopB" ]`. Ids must be unique across
the sources (`FulfilmentOrders/{shop}/{orderNumber}`); an id another source holds is refused.

## Owning a contract

The owner stores the class itself, in its own database, through its normal save path (`session.Store(new
FulfilmentShipment { Id = $"{orderId}/shipments/1", … })`), and grants each consuming module
`Replicate/{collection}` in `security.json`. The collection name is the class's: a store convention that renames
it breaks the contract (startup says so on the consumer side).

Natural ids make redeliveries no-ops; counters are cumulative, never deltas; states only move forward (§4.3).

## Versions

A changed shape is a new class in a new collection (`FulfilmentShipmentV2`), published beside the old one until
every consumer moved. There is no version number (WD13).
