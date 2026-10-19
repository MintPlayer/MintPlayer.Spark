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
   - The `ISaveReplacement`, if one governs the type: it may divert the whole save (to a proposal awaiting
     approval) and answer with a receipt. Then nothing below runs, and the caller gets 202 (§4a).
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
        manager.Retry.Action("Delete car", ["Delete"], message: $"Delete {car.LicensePlate}?", cancellable: true);
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

**Register it** with `spark.AddInterceptor<CarInterceptors>()`. The generated `spark.AddSparkDiscovered()` (which `AddSparkFull` calls) already
registers every interceptor in the project. A library registers
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
  included, is assigned when the row is stored, after the before-interceptors. `IsReplacedSaveApply` and
  `OnBehalfOf` are set when the save applies a diverted one (§4a).
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
  In a bulk delete, one row's cancel cancels the batch. An MCP plan treats any cancel, an update's included,
  as a refusal of the whole plan: it would otherwise be smaller than the one the user approved.

A refusal is safe in any order: everything any interceptor put in the session is evicted.

## 4a. Diverting a save: `ISaveReplacement`

A before-interceptor cannot skip the write. An `ISaveReplacement` can: it replaces the whole save with
something of its own, typically a document proposal that waits for approval (commerce PRD X7, the seam the
Approvals library builds on).

```csharp
public sealed class InvoiceProposals : ISaveReplacement
{
    public bool AppliesTo(Type entityType) => entityType == typeof(Invoice);

    public async ValueTask<SaveReplacementReceipt?> ReplaceAsync(SaveReplacementContext context)
    {
        var proposal = new Proposal
        {
            ObjectTypeId = context.PersistentObject.ObjectTypeId,
            Json = context.PersistentObjectJson,   // the wire JSON text, never the object
            BaseEtag = context.BaseEtag,           // null for a create
        };
        await context.GetSession().StoreAsync(proposal);
        return new SaveReplacementReceipt("approval", proposal.Id!);
    }
}
```

- **When it is asked.** After every gate the caller must pass (step 1 above), and before anything is mapped or
  loaded into the request session. No `IBeforeSave` or Actions hook runs for a diverted save: they run once,
  when the change is applied, as whoever applies it. So the proposer still needs the `New`/`Edit` right on the
  type, and an attribute they may not write is already gone from what is proposed.
- **A receipt diverts; `null` saves normally.** Throwing is a refusal, as anywhere else, and takes back what the
  replacement stored. What it stores in `context.Session` commits with the request.
- **The caller gets 202**, `{ result: { submitted: { kind, referenceId } } }`: not an error. ng-spark's create and
  edit pages show a "submitted for approval" notice and emit `submitted`; `SparkService.create`/`update` resolve
  to a `SparkSaveSubmitted` (check with `isSaveSubmitted`). The .NET `SparkClient` throws a
  `SparkClientException` with status 202 and the receipt as its body. Server-side code calling
  `IDatabaseAccess.SavePersistentObjectAsync` reads `PersistentObject.Replacement`. An MCP plan commits the
  proposal with the plan and says which operations were submitted rather than saved.
- **Store `PersistentObjectJson`, never the `PersistentObject`.** RavenDB serializes with Newtonsoft, which
  ignores the System.Text.Json converter that tells an AsDetail attribute from a plain one: nested rows would
  come back as plain attributes without their objects.
- **At most one per type**, like `IDeleteReplacement`. A second one is an error on the first save.

**Applying it** is `ISparkReplacedSaves.ApplyAsync`, called as the approver:

```csharp
var outcome = await replacedSaves.ApplyAsync(new ReplacedSave
{
    ObjectTypeId = proposal.ObjectTypeId,
    PersistentObjectJson = proposal.Json,
    BaseEtag = proposal.BaseEtag,
    OnBehalfOf = proposal.ProposedBy,
    // Commits with the apply, or not at all.
    Alongside = async session => (await ((IAsyncDocumentSession)session).LoadAsync<Proposal>(proposal.Id))!.Status = "Applied",
});
```

It saves through the same path as `/spark/po/create` and `/spark/po/update` (rights, row gates, shield,
validation, interceptors, WITH CHECK) with `Etag = BaseEtag`, in one commit with `Alongside`, and never asks the
replacement again. The outcome:

| Kind | Meaning |
|---|---|
| `Applied` | Saved; `Object` is the row as a load presents it. |
| `Cancelled` | An interceptor cancelled it; nothing was written. |
| `Conflict` | The row changed or went since the proposal (`ConflictReason` `changed`/`deleted`), or a create's natural id is taken (`exists`): answer 409, the proposal is stale. |
| `Unwritable` | The approver may not write attributes the proposal sets (`Attributes`). Saving would silently drop them, so nothing is written. |
| `Invalid` | Validation refused it (`Errors`). |

The interceptors see `SaveContext.IsReplacedSaveApply` and `OnBehalfOf` (also the `SparkFacts.OnBehalfOf`
fact), so authorship can name the proposer while the write is the approver's. A type whose change vector moves
by itself (a token refreshed hourly) goes stale for good; handle such a decision without a proposal.

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
  types with revisions), `SparkFacts.OnBehalfOf` (the proposer of an applied diverted save, §4a). Keep them small, and never put a secret in them: they are stored in an outbox
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
- **One `ISaveReplacement` per type**, consulted after the gates and before anything else of the save (§4a). A
  second one is a configuration error too.
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
