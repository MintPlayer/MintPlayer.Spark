# Claims: one owner per key, cluster-wide

A claim says "this key is mine": a seat held while the buyer pays, a per-buyer limit, a single-use
admission token, one queue entry per identity, an email address, the host that runs a singleton job.
`ISparkClaims` (`MintPlayer.Spark.Claims`, registered by `AddSpark`) keeps it in a RavenDB
compare-exchange value, so it holds across every node of a cluster, and every write is guarded by the
index it read: of two contenders, exactly one wins.

Decisions and evidence: [commerce_PRD.md](commerce_PRD.md) X12.

## A family of claims

```csharp
// Holds: an owner, an expiry, optional data. 30 seconds of grace for clock skew between hosts.
static readonly SparkClaimKind<SparkClaimHold> Seats = SparkClaimKinds.Holds("commerce/seats/", TimeSpan.FromSeconds(30));
```

A family is a key prefix (ending in `/`) plus how to read a stored value: who owns it
(`OwnerOf`) and when it expires (`ExpiresAtUtcOf`). The value can be any class. Spark's own
families keep the shapes they always had, so nothing stored had to be migrated:

| Family | Value | Expiry | Taken over when |
|---|---|---|---|
| `emails/` (`UserStore`) | the user id | never | the user document is gone |
| `oidc/client-ids/` (identity provider) | the application id | never | the application document is gone |
| `spark/messaging/leader` | `MessagingLease` | its `ExpiresAtUtc` | expired |
| `spark/dashboards/sampler` | `DashboardSamplerLeaseValue` | its `ExpiresAtUtc` | expired |

`IsAbandonedAsync` is how a family that never expires says a claim is dead anyway (its owner
document was deleted without releasing it).

## Reserve, convert, release

```csharp
var key = Seats.Key($"{eventId}/{seatId}");

var held = await claims.ReserveHoldAsync(Seats, key, owner: orderId, expiresAtUtc: now.AddMinutes(10));
if (!held.Holds) return SeatTaken();                 // Taken, or Conflict: someone else won

await claims.ConvertAsync(Seats, key, orderId);      // paid: the hold no longer expires
await claims.ReleaseAsync(Seats, key, orderId);      // cancelled: only the owner can release
```

| Status | Meaning |
|---|---|
| `Reserved` | written: a free key, or the takeover of an expired or abandoned claim |
| `AlreadyYours` | the caller already holds it; nothing written (reservations are idempotent by owner) |
| `Taken` | another owner holds a live claim |
| `Conflict` | the claim changed between the read and the guarded write |
| `Declined` | the caller's value function returned null |
| `Updated` / `Released` | the owner's update or release went through |
| `NotYours` | an update or release of a claim that is absent or another owner's |

- **Lazy expiry.** Nothing deletes an expired claim. Past `ExpiresAtUtc + Grace` it reads as absent
  (`GetAsync` answers null) and the next reservation takes it over, guarded by the index it read.
- **The value function sees what it takes over.** `ReserveAsync(kind, key, owner, previous => …)`
  gets the expired claim (null for a free key), so a lease can carry its fence forward, or decline
  (return null) when the previous value says the work was done.
- **`UpdateAsync`** changes the owner's claim in place: a renewal, new data. `ConvertAsync` is the
  update that clears a hold's expiry.

## Several keys and documents at once

```csharp
using var unitOfWork = claims.BeginUnitOfWork();
foreach (var seat in seats)
    if (!(await unitOfWork.ReserveHoldAsync(Seats, Seats.Key(seat), orderId, deadline)).Holds)
        return SeatTaken(seat);
await unitOfWork.Session.StoreAsync(order);
if (!await unitOfWork.CommitAsync())
    return TryAgain();                               // a key changed since it was read: nothing was written
```

The unit of work is one RavenDB cluster-wide session: every staged claim and document commits
together or not at all. Its statuses are what the commit will write; a key that changed meanwhile
fails the commit as a whole.

⚠️ RavenDB refuses optimistic concurrency (a change vector on `StoreAsync`) and patches in a
cluster-wide session: `Session` only loads, stores and deletes. Pass `atomicDocumentWrites: false`
when the documents it writes are also written by ordinary sessions, which leave no atomic guard to
check (`UserStore` deletes a user and its email reservation this way).

## Outside the container

`new SparkClaims(store, timeProvider, database)` builds one without dependency injection, which is
how the messaging and dashboard leases, the user store and the identity provider's client-id
reservation use it.
