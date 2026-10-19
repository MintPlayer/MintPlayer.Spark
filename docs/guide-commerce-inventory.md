# Commerce: the inventory engine

`MintPlayer.Spark.Commerce.Inventory` (alias `commerce-inventory`) decides whether something can be sold, and keeps
it from being sold twice. Checkout calls it; a host mostly defines quotas, item mappings and seat chains. The design
and its invariants are in `docs/commerce_PRD.md` §4.3; this guide is the how-to.

```csharp
builder.Services.AddSpark(builder.Configuration, spark => spark.AddCommerceInventory());
```

```json
"Spark": { "Commerce": { "Inventory": {
  "Grace": "00:00:02",              // a hold lives until ExpiresAt + Grace (I5)
  "MaxAttempts": 10,                // retries on a concurrency conflict
  "SingleSeatRule": "BetweenTakenOrRowEnd",
  "SweepSchedule": "*/5 * * * * *", // six fields = seconds
  "SweepBatchSize": 500,
  "PublishOnCommit": true
} } }
```

## Quotas and items

A **quota** is a capacity. Hot quotas are **sharded**: `CreateQuotaAsync("quotas/floor", 10_000, shardCount: 32)`
spreads the capacity over 32 documents, and a claim takes its units from one shard (another on a conflict), or
splits them over all shards when no single one has room. The shard count is the throughput lever (S2 was not
measured, round 4).

A sellable **item** counts against one or more quotas (D15). Map it once:

```csharp
await inventory.MapItemAsync("tickets/weekend", [new("quotas/saturday"), new("quotas/sunday")]);
await inventory.MapItemAsync("tickets/family-4", [new("quotas/saturday", Multiplier: 4)]);
```

`Items: [new("tickets/weekend", 2)]` in a claim then takes 2 units of each day; the claim fails as a whole when either
day is short (I2). `SetCapacityAsync` changes a quota's capacity, never below what is sold and held.

## Claims

```csharp
var result = await inventory.TryClaimAsync(new ClaimRequest(
    ClaimId: "claims/basket-17", Owner: "baskets/17", ExpiresAt: now.AddMinutes(10), Quotas: [],
    Items: [new("tickets/weekend", 2)],
    BestAvailable: [new("chains/block-a", 2)],
    Limits: InventoryLimitKeys.For("sales/42", max: 4, n: 2, new BuyerIdentifiers(AccountId: userId, Email: email))));
if (!result.Succeeded) /* result.Status: SoldOut, UnitTaken, LimitExceeded, NotTogether, LeavesSingleSeat… result.Subject names what refused */;
```

| Operation | When | Notes |
|---|---|---|
| `TryClaimAsync` | quantity entered (HoldFromSelection), "Pay" (HoldFromCheckout), payment confirmed (OnPayment, D1) | idempotent by claim id; `result.Units` lists the seats |
| `AdjustAsync` | the buyer changes a quantity | the request is the new target; all or nothing. On a **lapsed** claim it re-claims what is still free (late payment, D3/D16) |
| `ExtendAsync` | redirect to the provider (PaymentPending, D3) | refused once lapsed |
| `ConvertAsync` | paid | `lateLimits`: limit keys known only now (the card fingerprint) count at once or refuse the conversion |
| `ReleaseAsync` | a whole claim goes back | a held claim drops its holds; a converted one returns everything not returned yet |
| `ReturnAsync` | part of a converted claim goes back | idempotent by `ReturnId` |
| `RestockAsync` | shipped goods received back | puts a `ReturnStock: false` return's units back on sale, once |
| `ExchangeAsync` | seat change, upgrade (GB7) | return + claim + convert in **one** transaction; see below |
| `GetItemMappingAsync` | another package needs an item's quotas | the public read of `MapItemAsync`'s mapping; never load `InventoryItem` yourself |

### Exchanges (seat change, upgrade)

```csharp
var result = await inventory.ExchangeAsync(new InventoryExchange(
    ClaimId: "claims/checkout/line-1",
    Return: new("change:7f3a", ReturnReason.Other),          // everything, or Items/Quotas/Units for part
    Target: new ClaimRequest("claims/checkout/line-1/7f3a", "orders/12", now, [], Units: ["VenueLayoutSnapshots/…/A-12"])));
```

The return runs first, so what it frees is room for the target: an upgrade within one full quota succeeds. The target
is claimed and **sold** in the same cluster-wide transaction; a refusal (sold out, seat taken, limit) leaves the
original claim exactly as it was (I2). A seat the target names explicitly that the return gives back (a ticket type
upgrade on the same seat) is **handed over**: its compare-exchange value changes owner, because a key deleted in a
cluster-wide session cannot be created again in it. Seats given back are not offered to the target's chain fill in
the same exchange; they become holes once the target is seated. Repeating a done exchange answers success with the
target's seats. Checkout's back-office line change (`ChangeLineAsync`) uses it.

### Every return path, exactly once (I4)

Name the reason and key the return by the event that caused it, so a redelivered webhook changes nothing:

| Path | Call |
|---|---|
| hold expired | the sweeper: `ReleaseAsync(id, ReturnReason.Expired)` |
| payment failed / cancelled | `ReleaseAsync(id, ReturnReason.PaymentFailed)` |
| refund of a line | `ReturnAsync(id, new("refunds/r1", ReturnReason.Refund, Items: …))` |
| withdrawal (GA1) | `ReturnAsync(id, new("withdrawals/w1", ReturnReason.Withdrawal, Items: …, ReturnStock: !shipped))`, then `RestockAsync` on receipt |
| refused line of a mixed basket (D40) | `ReturnAsync(id, new(orderLineId, ReturnReason.RefusedLine, …))` |
| lost dispute (GB1) | `ReturnAsync(id, new(disputeId, ReturnReason.DisputeLost, Units: unusedSeats, …))`; a won dispute returns nothing |
| resale | `ReturnAsync(id, new(resaleId, ReturnReason.Resale, Units: …))` before the new buyer claims |

**Shipped goods** (§4.3, I4): a refund or withdrawal of units already shipped returns no quota (`ReturnStock: false`);
they become sellable again only at restock, from the shop's return disposition or the warehouse's restock event.
Per-buyer counts come back in both cases: name them in `Limits`, or let a full return give back all of them.

The withdrawal's refund timing (Directive 2011/83/EU Art. 13(3): unless the trader offered to collect the goods, it
may withhold the reimbursement until it received the goods back or the consumer supplied evidence of having sent them
back, whichever is earliest) belongs to checkout, which encodes and cites it (D37); the inventory only guarantees that
stock never returns before the restock.

## Seats

A seat is a numbered **unit**, claimed as an X12 hold `commerce/units/{unitId}`. A seat held past `ExpiresAt + Grace`
is taken over by the next claim; its old owner's conversion then answers `Expired`.

**Best available (chain fill, D25).** Define a chain per block group: rows in fill order, seats in fill order.

```csharp
await inventory.DefineChainAsync("chains/block-a", [["A1", "A2", "A3", "A4"], ["B1", "B2", "B3", "B4"]]);
```

A group gets the smallest hole before the cursor that seats it side by side, else the next run from the cursor; a
row tail too short is skipped and stays free for smaller groups. A group never spans two rows: when enough seats are
free but not together, the claim answers `NotTogether`. The chain document is the hot key of its seats, so **one
chain per block group** is the contention lever. A chain cannot be redefined once a seat was given out.

**Pick your seat (D7).** Pass the row with the picks: `Picks: [new SeatPick(rowUnits, ["R3", "R4"])]`. The server
refuses a pick that would leave a new single empty seat (`LeavesSingleSeat`, the subject names it): between two taken
seats (`BetweenTaken`) or also against the row's end (`BetweenTakenOrRowEnd`, the default); `Off` accepts anything.
A sale chooses per pick (`SeatPick.Rule`). The rule is a sales policy checked against the row as the transaction reads
it, not an invariant: two buyers picking in the same row at the same instant can together leave a single seat.

`GetUnitStatesAsync` answers Free, Held or Sold per unit for a seat map, never who holds it.

## Per-buyer limits (D16, D29)

`InventoryLimitKeys.For(scope, max, n, buyer)` makes one limit line per identifier the buyer has: account, visitor,
e-mail (lower case, `+tag` dropped, Gmail dots folded), address (upper case, no diacritics or punctuation) and card
fingerprint. Each is SHA-256 hashed into `{scope}/{kind}/{hash}`, so no address is stored in clear text (the hash is
pseudonymous, not anonymous: keep the database private). The scope is the sale or the whole event. A claim fails
when any one identifier would exceed its limit, so a buyer cannot dodge it by switching accounts.

## The sweeper and live availability

The sweeper (cron, `Commerce.Inventory.Sweep`, every 5 seconds) releases lapsed held claims, which frees their chain
positions and limit holds early, and calls every `IInventoryClaimLapsed` (discovered, `[SparkDiscoverable]`): that
is where checkout cancels an expired PaymentPending payment and promotions offer the units to the waiting list.
**Correctness never depends on it** (I3): a lapsed hold stops counting on its own.

The sweeper finds lapsed claims through the declared index `InventoryClaims/Lapsing` (X19,
`InventoryIndexes.LapsingClaims` with `[SparkIndexMap]`): the application's source generator emits it, so the
package ships no index class and no auto-index is created at run time. A stale result only delays a release.

**Commit hook.** After every commit, each `IInventoryCommitted` (discovered, `[SparkDiscoverable]`, scoped) gets an
`InventoryCommit`: the operation, the claim ids, the quotas whose availability changed and the **units whose state
changed** (taken, given back, held turned sold). Ticketing.Venues pushes seat-map frames from it. It is called inline
after the commit: keep it fast. An exception is logged; the commit stands.

After every commit that changes a quota, its availability is published on the public live topic
`commerce-inventory:{quotaId}` as `{ quotaId, available, soldOut }`, never the capacity or the sold count. Ticketing
models a date or a block as a quota of its own. Topics are in-process: on several instances, each publishes its own
commits.

## What the engine's documents may not do (S18)

Only cluster-wide sessions write `InventoryQuota`, `InventoryQuotaShard`, `InventoryItem`, `InventoryChain` and
`InventoryClaim`: a cluster-wide transaction refuses a document an ordinary session wrote meanwhile. So:

- they are **no persistent-object types**: startup refuses a model type whose CLR type is one of them. Read them
  through `ISparkInventory` (or a read-only query of your own); never write them through Spark CRUD;
- **no patches, counters, attachments or time series** on them;
- **revisions are off** on their collections: the package turns them off at startup, merging into the database's
  configuration. A failure is logged, not fatal;
- no correctness from `@expires` or `@refresh` (both can lag 36 hours on Community).

Per-buyer limit counters are plain compare-exchange values, not X12 claims: a limit is a counter many claims share,
and X12's claim has one owner. They are written in the same cluster-wide transaction, so I2 holds.

## Tests

`tests/MintPlayer.Spark.Commerce.Tests/Inventory`: `ChainFillTests` (pure), `InventoryEngineTests` (RavenDB, a
hand-driven clock), and `EngineCorrectnessSpikeTests`, the S1 concurrency guard at CI size (3 runs × 100 claimants;
`SPARK_SPIKE_S1_RUNS` / `SPARK_SPIKE_S1_CLAIMANTS` scale it, but load runs are dropped, round 4).
