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

Worked example in the repo: `WebhooksDemo`'s `GitHubProjectActions.GetRowFilterAsync` awaits the caller's GitHub-org allow-list (live, per-request-cached) and returns `p => owners.Contains(p.OwnerLogin)` — pushed down as `owner in (…)`.

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

### Projection queries fall back — never silently unfiltered

When a query returns an **index projection** (e.g. `VCar` from a `Cars_Overview` index), a predicate typed on the entity (`Car`) can't compose into `IRavenQueryable<VCar>`. The framework falls back automatically to post-materialization filtering: it loads the base documents for the page in **one batched request** and evaluates the compiled rule against those. A one-time diagnostic records the fallback. The result is filtered either way — the pushdown is an optimization, never the only gate.

## Write-side enforcement (`WITH CHECK`)

The row rule also guards writes, judged against the entity's **resulting** state (after mapping and `OnBeforeSaveAsync`, so any ownership stamping has happened):

- **create** → the new row must satisfy the rule (you can't create a document stamped with someone else's owner);
- **edit** → the *post-update* state must satisfy the rule, in addition to the pre-update check (you can't edit a row *into* someone else's scope).

Denial surfaces as **403** on create and **404** on update/delete (matching the read path: an authorized-but-forbidden instance is indistinguishable from not-found).

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

`CustomActionArgs.Parent` and `SelectedItems` are **server-loaded and row-checked**: the framework re-resolves the ids the client named through the row-gated read path before invoking your action. A denied or missing id is a 404 and your action never runs. The raw client payload stays available as `SubmittedParent` / `SubmittedSelectedItems` for actions that edit (treat those as untrusted). See [guide-custom-actions.md](./guide-custom-actions.md).

## The generic UI (`can` block)

For a row-scoped type, `GET /spark/{type}/{id}` attaches a `can: { edit, delete }` block to the PO, computed per row. `spark-po-detail` prefers it over the type-level permissions, so a row the caller may read but not edit doesn't render an Edit button that would 404. The block is **absent** for types with no row rule — clients fall back to `GET /spark/permissions/{type}` — so this is fully backward-compatible.

## ⚠️ Anonymous / public read — the row filter is the only gate

A common pattern: grant a type's `Query`/`Read` right to **`Everyone`** in `security.json`, then use `GetRowFilterAsync` to expose only the public subset. Coverage does exactly this — anonymous viewers see public repositories; authenticated viewers additionally see private repos their identity can access:

```csharp
public override async Task<Expression<Func<Repository, bool>>?> GetRowFilterAsync(string action)
{
    if (currentUser.IsAdmin) return null;                       // no restriction
    var owners = await orgAccess.GetAllowedOwnersAsync();       // empty for anonymous
    return r => !r.IsPrivate || owners.Contains(r.OwnerLogin);
}
```

**When you grant `Everyone`, the row filter is the only thing between the public internet and the entire collection.** There is no second gate behind it. Consequences to hold in mind:

- A bug that returns `null` (no restriction) instead of a restricting expression discloses **every row**, including private ones. Fail toward the restrictive branch: default `owners` to empty, and only widen it for an authenticated, authorized caller.
- The filter runs for **every** request to that type, authenticated or not. Don't assume a caller exists — an anonymous request has no user id.
- Row-level denial on this path is a filtered-out row, not an error — a mistake is silent. Test the anonymous case explicitly.

If you are not deliberately publishing a public subset, **do not grant `Everyone`** — scope the type-level right to an authenticated group and let the row filter refine within it.

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
- Overriding `OnLoadAsync` yourself takes over include responsibility on the detail path (the framework's default `OnLoadAsync` is what applies these).
