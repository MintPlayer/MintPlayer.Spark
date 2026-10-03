# Row-level security

Spark enforces authorization at two levels. **Entity-type** authorization — "may this principal query/read/edit/delete this *type* at all" — is configured declaratively in `security.json` (see the Authorization package). **Row-level** authorization — "may this principal act on *this specific row*" — is expressed in code, on the entity's Actions class, and enforced by the framework on every path that can return or write a row: detail get, list, query, streaming, breadcrumb reference loads, create, edit, and delete.

This guide covers the row-level layer end to end. It is auth-package-agnostic: it hooks the Actions classes and reads the principal from the request, needing nothing from `MintPlayer.Spark.Authorization`.

## The four hooks

All four are optional `virtual` members on `DefaultPersistentObjectActions<T>`. Override none and the type is unscoped (exactly as before). Override any and the framework does the rest — you never call these yourself.

| Hook | Purpose | Shape |
|---|---|---|
| `GetRowFilterAsync(action)` | The row rule as an expression the framework **pushes into the RavenDB query**. The primary hook. Construction may `await`. | `Task<Expression<Func<T,bool>>?>` |
| `IsAllowedAsync(action, entity)` | The row rule as a per-row predicate. A refinement, or a standalone for rules an expression can't capture. | `Task<bool>` |
| `GetProtectedAttributesAsync(action, entity)` | Names attributes of a row to **hide from this viewer** (per-row, per-attribute redaction). | `Task<IReadOnlyCollection<string>?>` |
| — | (create/edit write checks and the per-row UI `can` block are derived automatically from the two rules above.) | |

`action` is one of `"Query"`, `"Read"`, `"Edit"`, `"Delete"`, `"New"` — the same vocabulary as `IPermissionService`. Most rules ignore it; use it when a row is, say, readable by all but editable by few.

## `GetRowFilterAsync` — the primary hook

Prefer this. It expresses the rule as an expression, so the framework composes it into the RavenDB query and a list over a row-scoped type reads **only the caller's rows** instead of the whole collection. **Construction is async** — you can `await` an allow-list — while the returned expression stays synchronous and RavenDB-translatable.

```csharp
public class RepositoryActions : DefaultPersistentObjectActions<Repository>
{
    [Inject] private readonly IOrgAccess orgAccess;

    public override async Task<Expression<Func<Repository, bool>>?> GetRowFilterAsync(string action)
    {
        // Capture request-scoped data as locals so it lands in the expression as constants.
        var owners = await orgAccess.GetAllowedOwnersAsync();
        return r => !r.IsPrivate || owners.Contains(r.OwnerLogin);
    }
}
```

Worked example in the repo: `apps/CodeCoverage`'s `GitHubProjectActions.GetRowFilterAsync` awaits
the caller's GitHub-org allow-list (live, per-request-cached) and returns the shared predicate from
`GitHubProjectVisibility.Filter`, pushed down as `owner in (…)`.

Two spellings in that predicate are load-bearing rather than stylistic, and both are easy to get
wrong in a way that only shows up in production:

```csharp
project => project.Connection != RepositoryConnection.Disconnected
        && project.OwnerLogin.In(allowedOwners);
```

- **`In()`, not `Contains`.** RavenDB's LINQ provider fails two ways on .NET 10: a `string[]`
  receiver binds to the untranslatable `MemoryExtensions.Contains`, and `List<string>.Contains`
  throws `TypedParameterExpression`. `In()` also has a real in-memory implementation, which the
  compiled single-row checks rely on when they evaluate this same expression against one loaded
  document.
- **`!= Disconnected`, not `== Connected`.** An absent JSON field satisfies no equality, so
  equality hides every document written before the field existed. Negation matches the absent
  field, which is what makes the rule correct for old documents without a migration.

> **Cost contract (why awaiting I/O here is safe).** The framework invokes the hook **at most once per (entity type, action) per request** and caches the result — bounded by the model, never by row count, page size, or streaming batch count. On a stream the cache refreshes on the periodic re-authorization tick (~every 10 batches), so a filter is at most that stale. Because the result is cached per request, the filter must be a **pure function of request-scoped state**. `IsAllowedAsync`, by contrast, is genuinely per-row and is **not** memoized — express I/O-backed rules as a `GetRowFilterAsync` expression, not in `IsAllowedAsync`.

- **Return `null`** to mean *no restriction for this caller* — an administrator, say. The type still has a rule; this caller just isn't scoped by it.
- **Return a constant predicate** (`r => false`) for a caller who may see nothing. Constant predicates are evaluated in memory rather than pushed into RQL (RavenDB's provider need not translate them).
- The predicate's properties must be **queryable**: on a plain collection query, anything on the document; on a static index, the fields the index stores.

### Derivation — one rule, every path

You write the rule once; the framework derives every enforcement point:

- **Only `GetRowFilterAsync` overridden** → list paths push the expression into the query; single-row checks (detail, edit, delete) compile the same expression. List and detail cannot diverge — they're the same expression.
- **Only `IsAllowedAsync` overridden** → post-materialization filtering, exactly the original behavior.
- **Both** → **AND** semantics: the filter narrows, the predicate refines. Use this when part of the rule is expressible (`Owner == me`, pushes down) and part isn't (`&& !IsBusinessHoursLockout()`, per-row).

A startup diagnostic logs each row-scoped type and the mode it runs in.

### Projection queries push down when they can — never silently unfiltered

When a query returns an **index projection** (e.g. `VCar` from a `Cars_Overview` index), the predicate is typed on the entity (`Car`). The framework **rebinds it onto the projection by member name** (#285): when every member the predicate reads exists on `VCar` with the same type, the rebound predicate goes into the index query, so the database only returns rows the rule allows. When a member is missing — the index does not store `Owner` — it falls back to post-materialization filtering. Either way it then loads the base documents for the page in **one batched request** and evaluates the compiled rule against those. A one-time diagnostic records which branch ran. The result is filtered either way — the pushdown is an optimization, never the only gate.

⚠️ Rebinding is by **name and type**, not meaning. A projection property that shares a name and type with an entity property but holds something else (a display name in `Owner` where the entity holds an id) narrows the index query wrongly and hides rows the rule would allow (it never shows a row the rule forbids — the reload still judges the documents). Keep projection fields that a row rule reads identical to the entity's.

## Row policies — one rule for many types (#460)

A rule that is not about any one entity — soft deletion, tenancy, a moderation lock — is a **row policy**: written once, applied to every type it claims, ANDed with each type's own rule. Register it with `AddSparkRowPolicy<T>()` (scoped):

```csharp
public sealed class HideDeleted : RowFilterPolicy<ISoftDeletable>   // covers every implementer
{
    public override bool BypassInSystemContext => false;            // background work skips deleted rows too
    public override ValueTask<Expression<Func<ISoftDeletable, bool>>?> GetFilterAsync(RowPolicyContext context)
        => ValueTask.FromResult<Expression<Func<ISoftDeletable, bool>>?>(
            context.Deleted == SparkDeletedFilter.Exclude ? x => x.IsDeleted != true : null);
}

builder.Services.AddSpark(builder.Configuration, spark => spark.AddSparkRowPolicy<HideDeleted>());
```

- **Two kinds.** `IRowFilterPolicy` (helper `RowFilterPolicy<T>`) returns a predicate that is pushed into the query — paging stays in the database. `IRowCheckPolicy` (helper `RowCheckPolicy<T>`) judges each materialized row, which **switches database paging off** for the types it applies to; prefer a filter.
- **`AppliesTo(Type)`** decides which types a policy governs. The helpers use `typeof(T).IsAssignableFrom`, so an interface covers every implementer. The answer must depend on the type alone — it is cached process-wide.
- **How it composes.** Effective filter = the Actions class's `GetRowFilterAsync` AND every applicable filter policy, joined by rebinding each predicate onto one parameter and `AndAlso` (never `Expression.Invoke`, which RavenDB does not translate). A constant `false` from anyone short-circuits; a constant `true` is dropped. The combined filter goes where the Actions filter always went — before column filters and search — and is memoized per (type, action) per request exactly like it. Measured (#460 spike S1, Lucene and Corax): `where ((Owner = $p0 and IsDeleted != $p1)) and (Category = $p2) and (search(Title, $p3) or search(Body, $p4))`. Per-row rule = compiled filter AND `IsAllowedAsync` AND every check policy.
- **Rebinding is by member name.** A predicate over `ISoftDeletable` reads `x.IsDeleted`; it is rebound as `row.IsDeleted` on each entity (and on an index projection, #285). Every member it reads must exist on the target with the same type, or composing throws — at the first request that needs it — rather than silently dropping the predicate.
- **A policy returns a predicate, never a query.** It is not handed the `IQueryable` — RavenDB groups adjacent `Search` clauses and a policy-placed `Where` could be OR-ed into the search group.
- **Absent fields.** `x.IsDeleted != true` matches a document with no `IsDeleted` field; `!x.IsDeleted` / `== false` does **not** — measured on both engines, collection and static index (#460 spike S3). In memory the two agree, which is exactly why the database-side difference is a trap.
- **`RowPolicyContext`** carries the entity type, the action (including custom action names), the request's `Deleted` mode (the `deleted: exclude|include|only` field of a query request — core only carries it), `IsSystemContext` and the `User`.
- **System context.** Policies are skipped for the system by default (`BypassInSystemContext => true`), like Actions-class rules. A policy that returns `false` still applies to module sync and background work.
- **Which rules count as "declaring a row policy".** The anonymous-readable startup validator counts the Actions class's rule and policies with `IsVisibilityDecision => true` — a soft-delete filter on every type does not satisfy it. The `SparkQueryPage<T>` refusal counts the Actions rule, check policies and visibility policies, not a non-visibility filter policy. The per-row `can` block is computed whenever any rule or policy applies.
- **Everywhere `IRowSecurity` is asked**: list, detail, custom queries, sub-queries, distinct values, streams, breadcrumbs, the edit/delete gates, custom-action selections and `WITH CHECK`.

⚠️ **Known gap (D1, documented, not fixed):** an Actions class that overrides `OnLoadAsync` without calling the base takes over the read gate for its type — policies included. The write side has no such gap since #482: the framework owns every write, and `WITH CHECK` cannot be skipped. References are not row-checked on save.

## Persistence hooks (formerly interceptors; #460, #482)

Cross-cutting write behaviour — stamping, soft deletion, locks — is a persistence hook: a DI service implementing one interface per phase, registered with `spark.AddHook<T>()` (or found by the generated `AddHooks()`). The framework runs hooks only after every gate passed. See the [hooks guide](guide-hooks.md) for the whole sequence; in short:

| Hook | When |
|---|---|
| `IBeforeSave` | after mapping, **before** `WITH CHECK` and the write — mutate `context.Entity` to stamp it; `HookStage.Finalize` runs after every `Default` hook |
| `IAfterSave` | after the commit, each isolated |
| `IDeleteReplacement` | before any before-delete hook; returns whether the delete becomes a save of the entity (one per type) |
| `IBeforeDelete` | before the delete or its replacement is written; `context.IsReplaced` is final |
| `IAfterDelete` | after the delete, or the replacement, was committed |
| `IAfterLoad` | after an entity-backed object was loaded through the row-gated read path |

- **Order:** no numeric order. Within a phase, registration order (not a contract); the two structural rules are the replacement first and the `Finalize` stage last.
- **Replacing a delete** is decided by the framework before any hook, so nothing can defeat it: the tracked entity (as the hooks left it) is saved, and replication forwards a **save** rather than a hard delete (#460 spike S4). A purge (`DeletePersistentObjectAsync(typeId, id, PersistentObjectOperation.Purge)`) is never replaced.
- **Context:** `Operation` (`Save`, `New`, `Delete`, `Revert`, `Restore`, `Purge`, `Sync` — the explicit kinds come from the `IDatabaseAccess` overloads that take a `PersistentObjectOperation`; module sync passes `Sync`, which reaches only hooks with `HandlesSync`), the submitted `PersistentObject`, `Before` (the stored entity, from a separate session), `Entity`, `Id`, `User`, `IsSystemContext`, `Session`.
- **Refusing:** throw — `SparkRowLevelAccessDeniedException` is a 404, `SparkValidationException` a 400, `SparkCancelException` a silent no-op.

- **`INaturalIdCollision`** — a create of an `IHasNaturalId` type derived an id an existing row holds, and the row gate refused the caller that row. Core answers 404; a hook may throw its own exception first to explain (SoftDelete: "restore it instead"). Whatever it throws tells the caller about a row it may not see — explain only to callers entitled to know.

### Restore and purge are gated under their own names

`IDatabaseAccess` treats the two SoftDelete operations as their own verbs, so a row policy can confine them to deleted rows while `Edit` / `Delete` keep hiding those rows:

| Operation | Type-level right | Row-gate action | Refused by the disabled-action hook when withheld |
|---|---|---|---|
| `SavePersistentObjectAsync(po, Restore)` | `Restore/T` | `"Restore"` | `Restore`, `Edit`, `Save` |
| `DeletePersistentObjectAsync(typeId, id, Purge)` | `Purge/T` | `"Purge"` | `Purge`, `Delete` |
| `SavePersistentObjectAsync(po, Revert)` (History) | `Revert/T` **and** `Edit/T` | `"Revert"` | `Revert`, `Edit`, `Save` |

A restore or revert never creates: an id that names nothing is a 404. The `WITH CHECK` after a restore or revert still asks `"Edit"`.

**The Actions class sees the base verb.** Row *policies* get the real name (`RowPolicyContext.Action` is `"Restore"`, `"Purge"`, `"Revert"`) — that is how SoftDelete confines a restore to a deleted row. An Actions class's own `GetRowFilterAsync` / `IsAllowedAsync` is asked about the base verb instead: `"Edit"` for a restore or revert, `"Delete"` for a purge. So a rule written for the built-in verbs ("only the owner may edit") governs them too, instead of an unfamiliar name falling through to "unrestricted" (#460, M7). The packages that use all this are [`MintPlayer.Spark.SoftDelete`](../libs/soft_delete/MintPlayer.Spark.SoftDelete/README.md) and [`MintPlayer.Spark.History`](../libs/history/MintPlayer.Spark.History/README.md) — use them rather than a hand-written soft-delete policy or revert.

If a hook refuses a delete after the replacement changed the entity (SoftDelete marked it, a lock said no), `IDatabaseAccess` evicts the entity from the request session, so no later save in the request writes the half-made change.

## Write-side enforcement (`WITH CHECK`)

The row rule also guards writes, judged against the entity's **resulting** state (after mapping and the before-save hooks, so any ownership stamping has happened):

- **create** → the new row must satisfy the rule (you can't create a document stamped with someone else's owner);
- **edit** → the *post-update* state must satisfy the rule, in addition to the pre-update check (you can't edit a row *into* someone else's scope).

Denial surfaces as **403** on create and **404** on delete (matching the read path: an authorized-but-forbidden instance is indistinguishable from not-found). An **update** of a row the caller can no longer read answers **409 `deleted`**, the same answer as a row that is gone or soft-deleted (#467, D15/D30c). One answer for all three keeps it from being an existence oracle, and the form tells the user "somebody else deleted this record" instead of re-creating it.

### Bulk writes go through the Read gate too (#467, D11/D12)

A selection is ids the browser sent, so the server re-fetches the rows before acting on them:

- `/spark/po/delete-many` and a custom action on a selection need the **`queryId`** the rows were ticked in. The rows are fetched **through that query**, with its right, filter, row filter and parent, and must each be **readable** (the `Read` right and the `Read` row rule, in the list's deleted mode).
- A row that is missing, outside the query or unreadable refuses the **whole** request with 404, exactly like a missing id, and is never named.
- Then each row passes its own `Delete` gates (the right and the `Delete` row rule) and `OnDisableActionsAsync`. A row that fails those is named in the refusal (D18), because it already passed the Read gate and naming it discloses nothing.
- Every row carries the etag it was listed with; a row changed since then is a 409 (D14).

So naming another query of the same type cannot widen a selection past what its own list shows. The detail is in [guide-custom-actions.md, "The bulk Delete"](./guide-custom-actions.md#the-bulk-delete).

> **⚠️ Service / machine accounts and the create check.** Because the rule now runs as a `WITH CHECK` on create, a per-user ownership filter (`car => car.CreatedBy == userId`) will **reject a create by a principal that has no user id** — a machine / client-credentials token with type-level `New` rights but no `sub` claim. Such a principal has the right to create but no identity to own the row by, so a filter that returns `car => false` for "no user" blocks it. Return `null` (unrestricted) for authenticated service principals — treat "no user id but authenticated" as a machine, and reserve the deny-everything branch for a *truly anonymous* caller. Fleet's `CarActions.GetRowFilterAsync` shows the pattern.

## Per-viewer attribute redaction

`GetProtectedAttributesAsync` names attributes of a specific row that this caller must not see. The framework nulls their values and marks them invisible at mapping time, on every read path:

```csharp
// A secret only managers of this repository may view.
public override Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Repository entity)
    => CanManage(entity)
        ? Task.FromResult<IReadOnlyCollection<string>?>(null)
        : Task.FromResult<IReadOnlyCollection<string>?>(["BadgeToken"]);
```

- Redaction **nulls, not omits**: the attribute stays in the payload with `Value = null` and `IsVisible = false`. Dropping it would break name-indexed clients and leak the rule via a schema mismatch.
- A **dotted name** (`"Jobs.Salary"`) redacts a column inside an AsDetail attribute's embedded rows — the one place a row filter can't reach, since embedded rows aren't rows.
- **Write-back is shielded**: a client that received a redacted (nulled) value and submits the form back cannot clobber the stored secret — protected attributes are restored to their stored value before the merge.
- Zero cost for types that don't override the hook.

## Custom actions

`CustomActionArgs.Parent` and `SelectedItems` are both produced **server-side** from the ids the client named — neither is the object the browser posted. `Parent` goes through the row-gated read path; `SelectedItems` is re-materialized by re-running the query the rows came from (#327), so it carries that query's own projection. A denied or missing id refuses the whole request with a 404 and your action never runs. The submitted parent stays available as `SubmittedParent` for actions that edit (treat it as untrusted); there is no submitted form of a selection, because a row was never a document. See [guide-custom-actions.md](./guide-custom-actions.md).

## The generic UI (`can` block)

For a row-scoped type, `GET /spark/{type}/{id}` attaches a `can: { edit, delete }` block to the PO, computed per row. `spark-po-detail` prefers it over the type-level permissions, so a row the caller may read but not edit doesn't render an Edit button that would 404. The block is **absent** for types with no row rule — clients fall back to `GET /spark/permissions/{type}` — so this is fully backward-compatible.

Each value is the **intersection** of the caller's type-level right and the row rule (#243): a `QueryRead`-only grant yields `can: { edit: false, delete: false }` no matter what the row rule says, so the block never claims more than `GET /spark/permissions/{type}` would. You do **not** need to special-case write actions in `GetRowFilterAsync` (e.g. return `x => false` for `"Edit"`/`"Delete"`) on a read-only surface — the type-level right already caps the block.

## ⚠️ Anonymous / public read — the row filter is the only gate

A common pattern: grant a type's `Query`/`Read` right to the **`anonymous`** well-known group in `security.json`, then use `GetRowFilterAsync` to expose only the public subset. Coverage does exactly this — anonymous viewers see public repositories; authenticated viewers additionally see private repos their identity can access:

```csharp
public override async Task<Expression<Func<Repository, bool>>?> GetRowFilterAsync(string action)
{
    if (currentUser.IsAdmin) return null;                       // no restriction
    var owners = await orgAccess.GetAllowedOwnersAsync();       // empty for anonymous
    return r => !r.IsPrivate || owners.Contains(r.OwnerLogin);
}
```

**When you grant `anonymous`, the row filter is the only thing between the public internet and the entire collection.** There is no second gate behind it. Consequences to hold in mind:

- A bug that returns `null` (no restriction) instead of a restricting expression discloses **every row**, including private ones. Fail toward the restrictive branch: default `owners` to empty, and only widen it for an authenticated, authorized caller.
- The filter runs for **every** request to that type, authenticated or not. Don't assume a caller exists — an anonymous request has no user id.
- Row-level denial on this path is a filtered-out row, not an error — a mistake is silent. Test the anonymous case explicitly.

If you are not deliberately publishing a public subset, **do not grant `anonymous`** — grant `authenticated` (or a named group) instead and let the row filter refine within it.

⚠️ Note that `anonymous` is **not** the old `Everyone`: it applies only while the caller has not signed in. A right that both an anonymous visitor and a signed-in user should have is two grants. And never simply *delete* a type-level grant to lock a type down — type-level rights gate row rules, so with no grant at all `GetRowFilterAsync` never runs and signed-in callers are denied too.

## System context (module sync)

Module-to-module sync (authenticated via mTLS) writes on behalf of other modules' users; a viewer-scoped rule must not refuse it. Such requests carry an explicit `SparkSystemContext` claim and are exempt from row rules — entity-type rights (`security.json`'s `Module:*` groups) still govern which types a module may touch.

The exemption is **positive-claim-only** and fails closed: the mere absence of an HTTP request is *not* system context (that's the default state of tests and every non-request path). If you cannot prove the caller is the system, the caller is treated as a viewer and the rules apply.

## Reducing round-trips — `GetDefaultIncludes()`

Complementary to the row filter's memo (which bounds how often *your hook* runs), `GetDefaultIncludes()` bounds how often the *framework* round-trips for referenced documents. Properties decorated `[Reference(typeof(X), "GetX")]` are already auto-included; override `GetDefaultIncludes()` to add more:

```csharp
public override IReadOnlyCollection<string>? GetDefaultIncludes() => ["Company", "Address.City"];
```

- Paths are **dotted JSON paths into the document**: `"Company"` for a top-level reference id, `"Address.City"` for an id nested inside an **embedded** object.
- They do **not cross a document boundary** — RavenDB has no recursive include, so a chain through a referenced *document* (`Car → Owner → Owner.Company`) can't be expressed here. Use an index that projects the deeper id, or let the breadcrumb resolver's batched load handle it.
- Applied on detail, list, and query. On a **stream** the framework can't apply them (your `StreamItems` builds its own query) — include what you need in that query yourself.
- A path whose first segment isn't a property of the type logs a one-time warning and includes nothing.
- Overriding `OnLoadAsync` without calling the base takes over the whole detail read pipeline — the includes, but also the collection guard, the row-level Read gate, redaction, the per-row `can` block and the etag all live in the default `OnLoadAsync` (#324). The typical override calls `await base.OnLoadAsync(id, parent)` and decorates the result.

## Distinct-value lists are a security surface (#431)

`POST /spark/queries/distinct-values` returns the values behind a column's filter panel. It is the
strongest read surface in the query pipeline, and it is built accordingly.

**Values are computed in memory, over rows the gate has already secured** — never by a RavenDB facet.
A facet aggregates in the database, where the row filter frequently is not: `ComposeRowFilterAsync`
refuses to compose into a projection query, which is the default shape for an indexed query, and the
gate that filters those runs after materialization. A facet on such a query would publish values drawn
from rows the caller cannot read — the same oracle class as the `?sortColumns=` hardening, except
returning the values rather than leaking their order.

It is also the only place reference columns can work at all: breadcrumb text is resolved after
materialization and is not an index term, so a facet has nothing to aggregate.

Three rules the implementation keeps, and a reviewer should check:

1. **Refused and empty are the same answer.** A column the caller may not enumerate and a column with
   nothing to list return the same object. The difference is a fact about their rights.
2. **A denied reference is dropped, not shown.** It arrives carrying the redaction placeholder rather
   than a name; listing it would confirm the row exists.
3. **No per-value counts, ever.** A count is a cardinality oracle for rows the caller may not see —
   the same refusal already recorded for an author-supplied `TotalItems`.
