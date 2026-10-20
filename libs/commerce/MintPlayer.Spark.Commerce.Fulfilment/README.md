# MintPlayer.Spark.Commerce.Fulfilment

The shop side of the fulfilment contracts (alias `commerce-fulfilment`; commerce PRD §4.9, warehouse PRD §4.3–§4.4):
a webshop built on `MintPlayer.Spark.Commerce.Shop` hands its goods to a warehouse app through
[`MintPlayer.Spark.Fulfilment.Contracts`](../../fulfilment/contracts/README.md). State flows both ways, every fact has one
owner, and neither app writes the other's documents (WD2).

| Contract | Direction | What the shop does |
|---|---|---|
| `fulfilment-orders` | publishes | one per confirmed shop order (per seller); ids `FulfilmentOrders/{ShopName}/{orderNumber}/{seller}/{revision}` |
| `catalog-items` | publishes | every goods variant with a SKU (catalog sweep, and before an order) |
| `expected-returns` | publishes | Checkout's returns of shipped goods, with the return reason per line (GA8) |
| `fulfilment-status` | consumes | cancellation answers, rejections, cancelled remainders |
| `shipments` | consumes | tells Shop what shipped and was delivered (the withdrawal and guarantee periods start there) |
| `stock-levels` | consumes | sets each variant's quota: `OnHandSellable − ReservedOtherChannels − buffer + ShippedTotal + baseline` |
| `return-receipts` | consumes | registers the receipt with Checkout (the withheld refund is paid, CRD Art. 13(3)) |

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddMessaging();
    spark.AddReplication();
    spark.AddMolliePayments();
    spark.AddCommerceShipping().UseSendcloud();
    spark.AddCommerceShop();          // "Spark:Commerce:Shop": { "Fulfilment": "Warehouse" }
    spark.AddCommerceFulfilment();
});
```

```json
{
  "Spark": {
    "Commerce": { "Fulfilment": { "ShopName": "shop-a", "Channels": { "shop": { "Buffer": 0 } } } },
    "Replication": {
      "Sources": {
        "FulfilmentOrderStatuses": [ "Warehouse" ],
        "FulfilmentShipments": [ "Warehouse" ],
        "FulfilmentStockLevels": [ "Warehouse" ],
        "FulfilmentReturnReceipts": [ "Warehouse" ]
      }
    }
  }
}
```

In the application's `security.json`, the warehouse's module group (`Module:Warehouse`) gets, per published contract, the
registration grant and the read right its fields are checked against (warehouse §4.2.6, F17). A library may not grant on
collection names (SPARK047), so these six rights are the application's:

| Resource | Why |
|---|---|
| `Replicate/FulfilmentOrders`, `Read/FulfilmentOrder` | orders |
| `Replicate/FulfilmentCatalogItems`, `Read/FulfilmentCatalogItem` | catalog items |
| `Replicate/FulfilmentExpectedReturns`, `Read/FulfilmentExpectedReturn` | expected returns |

The warehouse grants this shop's module `Replicate/…` on the four collections it owns. The three published types are model
types of this library (so replication can capture and authorize them); no right grants them to people.

## The rules it keeps

- **Cancellation is a status**, never a delete: the shop sets `CancellationRequested` on its own order and the warehouse
  answers in its status. **An edit is a cancellation plus a new order** (fewer units after a refund, another address via
  `ISparkCommerceFulfilment.ChangeAddressAsync`), published once the cancellation was accepted.
- **`sold` is lowered only on a confirmed cancellation** (§4.4 rule 1): Checkout's `ICheckoutStockFeed` seam keeps a refunded
  warehouse line's units sold; they go back on sale when they can no longer ship. A unit that shipped anyway stays sold.
- **Undeliverable units are refunded automatically** (commerce PRD D45): units the warehouse cancels on its own are
  refunded at their charged amount (order discount and fees included), and the whole delivery cost once no goods of the
  order remain; the buyer gets the refund mail. CRD Art. 18(2)–(3) ("all sums paid under the contract"), checked against EUR-Lex 02011L0083-20260927 on 2026-10-20. Only a
  refund Checkout refuses or the provider fails is flagged for staff.
- **One epoch** (rule 2): `SetBaselineAsync(variantId, units)` at onboarding adds back what the shop shipped itself.
- **Bundles** (rule 3) sell through inventory quota multipliers over their components' variant quotas, which the feed sets.
- **I4 exception** (rule 4): Checkout restocks nothing for a warehouse line; the warehouse's restock raises its on-hand.
- **Refund timing** (WD12, GA1): on the warehouse's receipt by default, or at once with `RefundTiming = OnRequest`; a
  withdrawal is also paid on the buyer's proof of dispatch, whichever is earlier (staff register it in Checkout's aftercare).
- **Recall (GA7)**: `TraceRecallAsync(sku, lot)` resolves the warehouse's shipments of a lot to buyers and
  `SendRecallNoticeAsync` mails each once (`SparkFulfilment/RecallNotice`).

Everything is re-derived from state (Checkout's order and refunds, the replicas) by order events, replica notifications and
a reconcile job (`ReconcileSchedule`, every minute), which is also the only path on the ETL transport.
