# Guide: persistence interceptors

Spark owns every write of a persistent object. It loads the row, maps the posted values onto it,
checks it, writes it with the version the caller saw, and commits once. Your code runs at fixed
points in that sequence through **interceptors**: small DI services that implement one interface per phase.

Before #482 an interceptor implemented one interface for every phase (`IPersistentObjectInterceptor`),
and the Actions class had its own save and delete methods (`OnSaveAsync`, `OnBeforeSaveAsync`,
`OnDeleteAsync`, …). Both are gone. The namespace is still `MintPlayer.Spark.Abstractions.Interceptors`.

## 1. What the framework does, in order

A save (create, edit, revert, restore, sync):

1. Every gate: the type right, the row gate, the etag check, disabled actions, the attribute write shield, validation.
2. Load the stored row into the request session (an edit) and run the `IAfterMaterialize` interceptors.
3. `MapAsync(obj, existing)` on the Actions class — the posted values onto the row, or a new instance.
4. The `IBeforeSave` interceptors: `InterceptorStage.Default` ones, then `InterceptorStage.Finalize` ones.
5. **WITH CHECK**: the row rule, judged on the row as the interceptors left it.
6. Store it with the expected change vector, store one outbox message per durable after-commit interceptor
   (`IAfterSaveCommitted`), and **commit once** (`SaveChanges`): the row and its follow-ups together.
   Serving an HTTP request, the framework then waits — bounded, never failing — for the indexes over the
   collections that commit wrote (§1a).
7. The `IAfterSave` interceptors, each isolated.

A delete (and a bulk delete, row by row, committed once for all rows):

1. Every gate, as above.
2. The `IDeleteReplacement`, if one governs the type (SoftDelete): it decides whether the delete
   becomes a save of the row.
3. The `IBeforeDelete` interceptors (stages as above). `context.IsReplaced` is final here.
4. Store the replacement, or delete — both with the expected change vector — store one outbox message
   per durable interceptor (`IAfterDeleteCommitted`), and commit once — then, in a request, the same
   index wait (§1a).
5. The `IAfterDelete` interceptors, each isolated.

Later, outside the request: Messaging delivers each outbox message, and the durable interceptor runs (§5a).

### 1a. The caller's next query sees the write

A write made while serving an HTTP request commits first; the commit's outcome is final and is what the
request answers. Then, separately, the framework waits until every index (static or auto) over a
collection the commit wrote has processed it, so the same user's next query — a grid re-fetching after
a save, a sub-query membership check in a bulk delete — does not read a stale index. The wait is
**best-effort and bounded at 15 s**: an index still behind then, or one disposed during the wait (an
auto-index merge, a side-by-side swap, a reset), is logged as a warning and the request is answered
normally. A paused index costs every write to its collections the full 15 s; the warning names it.

This covers everything that commits through `IDatabaseAccess` — including documents an interceptor
stores in `context.Session`, which commit in the same `SaveChanges`. Scopes without an `HttpContext`
(message handlers and durable interceptors, cron jobs, migrations, replication, hosted services) do not
wait. Code that calls `SaveChangesAsync` on a session itself does not wait either; see
[the custom actions guide](guide-custom-actions.md) for how to get the same behaviour.

Nothing in this list can be skipped: not by an interceptor, not by an Actions class.

## 2. Writing an interceptor

Implement the typed interface for the type you care about:

```csharp
public sealed class CarInterceptors(IManager manager) : IBeforeDelete<Car>, IAfterSave<Car>
{
    public ValueTask OnBeforeDeleteAsync(Car car, DeleteContext context)
    {
        manager.Retry.Action("Delete car", ["Delete", "Cancel"], message: $"Delete {car.LicensePlate}?");
        if (manager.Retry.Result!.Option == "Cancel")
            throw new SparkCancelException();
        return ValueTask.CompletedTask;
    }

    public ValueTask OnAfterSaveAsync(Car car, SaveContext context)
    {
        manager.Client.Notify($"Car {car.LicensePlate} saved", NotificationKind.Success);
        return ValueTask.CompletedTask;
    }
}
```

`IBeforeSave<T>` governs `T` and every type assignable to it, so `IBeforeDelete<ISoftDeletable>`
covers a whole kind. For a rule the type alone cannot express, implement the untyped interface and
`AppliesTo(Type)`. The answer must depend on the type only; it is cached.

**Register it** with `spark.AddInterceptor<CarInterceptors>()`. With `AddSparkFull`, the generated `AddInterceptors()` already
registers every interceptor in the project. Without it, call `spark.AddInterceptors()` yourself. A library registers
its interceptors inside its own `AddXxx()`.

**Or put it on the Actions class.** An Actions class may implement interceptors for its own type
(`CarActions : DefaultPersistentObjectActions<Car>, IBeforeDelete<Car>`). It needs no registration.
The framework resolves the Actions class once per request and runs it after the registered interceptors of
the same stage. State therefore carries from its before-interceptor to its after-interceptor. CodeCoverage's
`ApiTokenActions` uses this to show a freshly minted token once.

## 3. The context

Every context carries `EntityType`, `User`, `IsSystemContext`, and `Session`: the RavenDB
session the write commits through (`context.GetSession()` returns it typed).

- `SaveContext`: `Operation`, `PersistentObject` (the posted object, minus what the caller may not
  write), `Entity`, `Id`, `Before` (the stored row from a separate session; null for a create),
  `UnwritableAttributes`, `IsNew`. **`Id` is null in a before-interceptor of a create**: the id, natural ids
  included, is assigned when the row is stored, after the before-interceptors.
- `DeleteContext`: `Operation`, `Id`, `Entity`, `Reason`, `IsPurge`, `IsReplaced`.
- Both: `Facts`, small strings a before-interceptor records for the durable interceptors (§5a).

## 4. What a before-interceptor may do

- **Validate**: throw `SparkValidationException` (400) or `SparkRowLevelAccessDeniedException` (404).
- **Mutate the entity.** What it stamps is what WITH CHECK judges and what is written. It cannot swap
  the entity for another instance.
- **Store side documents** in `context.Session`. They commit with the write, and are taken back when
  anything refuses it.
- **Prompt** with `manager.Retry.Action(...)`. The request is answered with a 449, and replayed
  with the answer, so an interceptor must be idempotent up to its prompt. A prompt during a **bulk** delete is
  refused: that row is named in the refusal ("asks for a confirmation; delete it on its own").
- **Cancel**: `throw new SparkCancelException()`. This is not an error. Nothing is written, nothing the
  interceptors stored is kept, and no after-interceptor or replication runs. The client gets a no-op success: 204 for
  a delete or a delete-many, 200 with the object as stored for an update, 204 for a create.
  In a bulk delete, one row's cancel cancels the batch.

A refusal is safe in any order: everything any interceptor put in the session is evicted.

## 5. After-interceptors

After-interceptors run after the commit, and each is isolated. A failure is logged and never turns the
committed write into an error, nor skips the interceptors after it. Use them for in-request follow-ups: a
toast, a cache invalidation, a shown-once secret. Work that must eventually happen even if the process
dies right after the commit belongs in a durable interceptor (§5a).

## 5a. Durable after-commit interceptors

`IAfterSaveCommitted` and `IAfterDeleteCommitted` (typed: `IAfterSaveCommitted<Order>`) run **after
the commit, for certain**. The framework stores one outbox message per row and durable interceptor **in the
write's own commit**, and Spark Messaging delivers it with its retries and dead-lettering. A committed
write therefore always gets its follow-ups, even when the process dies right after the commit, and a
refused, cancelled or conflicting write never does: its messages were in the commit that did not
happen.

```csharp
public sealed class OrderMail(IMailer mailer) : IBeforeSave<Order>, IAfterSaveCommitted<Order>
{
    public ValueTask OnBeforeSaveAsync(Order order, SaveContext context)
    {
        context.Facts["Customer"] = order.CustomerEmail;   // the durable interceptor sees no entity
        return ValueTask.CompletedTask;
    }

    public Task OnAfterSaveCommittedAsync(SparkCommittedChange change, CancellationToken ct)
        => change.IsNew ? mailer.SendAsync(change.Facts["Customer"], $"Order {change.Id} received", ct) : Task.CompletedTask;
}
```

- **The payload, never the entity**: `EntityType` (full name), `Id`, `Operation`, `IsNew`,
  `IsReplaced`, `IsPurge`, `UserId`, `IsSystemContext`, `OccurredAt`, `PreviousChangeVector`, and
  `Facts`. The row may have changed again by the time the interceptor runs; load it when its current state
  matters. There is **no new change vector**: the message is written before the database assigns one.
- **Facts** are what a before-interceptor copies from the entity for later: `SparkFacts.Reason` (the delete
  reason, set by the framework and SoftDelete), `SparkFacts.ChangedAttributes` (set by History for
  types with revisions). Keep them small, and never put a secret in them: they are stored in an outbox
  document. A secret shown once (CodeCoverage's API token) stays an in-request `IAfterSave`.
- **At least once, outside any request**: the interceptor runs in its own DI scope with no HTTP context, and a
  redelivery can repeat it. Make it idempotent. Throw to retry.
- **Messaging is required.** A registered durable interceptor without `spark.AddMessaging()` is a startup
  error. A library that ships one references Messaging (Moderation does).
- **Client-assigned ids only**: a type using server-assigned ids (`Orders|`) cannot have durable interceptors,
  since its id does not exist until the commit the message is written in. Spark assigns ids on the
  client by default.
- A durable interceptor that is removed from the app while messages for it are still queued is dead-lettered
  ("not registered in this app").

They replace SoftDelete's `ISoftDeleteObserver` and History's `ISparkRevisionObserver`. Moderation's
vote reversal and the DemoApp broadcasts are durable interceptors.

Publishing a message of your own inside a write works the same way: `IMessageOutbox.EnqueueAsync(session,
message)` stores it in the write's session, and it commits with the write.

## 6. Order

There is no numeric order. Within a phase, interceptors run in registration order, with the Actions class
last. That order is not a contract. Two structural rules replace the old numbers:

- **One `IDeleteReplacement` per type**, consulted before any before-delete interceptor. A second one for
  the same type is a configuration error.
- **`InterceptorStage.Finalize`** before-interceptors run after every `Default` one. History stamps there, so its
  "did this edit change anything?" sees the row as it will be written.

## 7. Replication (sync)

A write that arrives from the owner module (`PersistentObjectOperation.Sync`) reaches only interceptors that
opt in with `HandlesSync => true`. The owner already ran its own interceptors. The library interceptors that must
see a sync (History, Contributions, Replication's own forwarder) opt in; app interceptors normally do not.

A sync runs under the sending module's certificate, so `ISparkCurrentUser.Id` is `null` there. The user
the replica made the edit for travels with the action (`SyncAction.InitiatorId`). During the save it is
available as `ISparkSyncInitiator.UserId`, which History stamps `ModifiedBy` with (#271). Use it for
stamping only, never to authorize.

## 8. Raw writes and SoftDelete

With the Actions class's save and delete methods gone, the interceptors are the only path to the database
that runs the replacement. SoftDelete also guards the side door. A raw `session.Delete` of an
`ISoftDeletable` document is refused at commit, unless the framework issued it (a purge) or the code
runs inside `SparkRawWrites.Allow()` (a migration, a test fixture). Patches and delete-by-query raise
no session event, so they are not covered.
