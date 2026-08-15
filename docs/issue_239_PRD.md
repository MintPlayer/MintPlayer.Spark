# PRD — Issue #239: async `GetRowFilterAsync` + RavenDB 30-request-cap safety

- **Issue:** [#239](https://github.com/MintPlayer/MintPlayer.Spark/issues/239) (async row filter, approved direction: async-all-the-way).
- **Branch:** `feat/async-row-filter`.
- **Supersedes:** the M7 stub in `docs/issue_236_plan.md` (that section now points here).
- **Status:** Proposed. Compiled from issue #239 plus a dedicated two-agent cap investigation (2026-08-15): an invocation-count + session-request audit, and a mitigation-design comparison. All `file:line` against `master` @ 5d5fce0 (preview.44).

## 1. Problem

Two problems, and the second is the reason this PRD exists separately from #239.

**(a) The hook can't `await`.** `DefaultPersistentObjectActions<T>.GetRowFilter(string action)` (`Actions/DefaultPersistentObjectActions.cs:150`) is the only synchronous virtual on the class — its siblings `IsAllowedAsync` and `GetProtectedAttributesAsync` are `Task`-returning. Real row rules need async data: Coverage's `Repository` rule needs `await gitHubAccess.GetAllowedOwnersAsync()` (identity store → token service → GitHub HTTP on cache miss); its `Commit` rule wants `c => c.Repository.In(visibleRepoIds)` where `visibleRepoIds` comes from a RavenDB query. WebhooksDemo's `GitHubProjectActions` is stuck on post-materialization `IsAllowedAsync` (no pushdown, no WITH CHECK) purely because the expression hook can't await.

**(b) An async hook that does I/O will silently blow RavenDB's 30-request-per-session cap.** This is the concern that makes (a) dangerous to ship naively. RavenDB sessions cap at `MaxNumberOfRequestsPerSession = 30` (never overridden in production — `SparkMiddleware.cs:107` keeps Raven's default deliberately). The hook is invoked **many times per request**, and every invocation that queries Raven on the request session spends against that 30. The consumer must be protected **by construction, with a severe margin** — not by a documentation footnote.

## 2. Investigation findings (verified against code)

### 2.1 Hook invocation counts — bounded by *data*, not the model, today

The hook runs from three sites: `RowSecurity.InvokeGetRowFilter` (`RowSecurity.cs:392`, via `ComposeRowFilter:196` and `ResolveEffectiveRule:366`), and directly in `EnsureRowSaveAllowedAsync` (`DefaultPersistentObjectActions.cs:81`). Nothing is memoized.

| Path | Hook invocations (R = referenced docs the breadcrumb resolver loads) |
|---|---|
| Detail read (`GetPersistentObjectAsync`) | **3 + R** — `Read`/`Edit`/`Delete` at `DatabaseAccess.cs:107,124,125`, plus one `Read` **per referenced document** at `BreadcrumbResolver.cs:121` |
| List / query page | **2 + R** — compose (`QueryExecutor.cs:167`) + `FilterAsync` (`:181`), plus breadcrumbs ×R |
| Streaming, per batch | **(0–1) + R_batch**; **across the connection: unbounded** (re-invoked every batch, forever) |
| Create / Edit | 1 (`EnsureRowSaveAllowedAsync`); a full PUT is 5 + R (it does a detail read first) |
| Custom action, S selected | **3(S+1) + R** — a full detail read for the parent **and once per selected item, in a loop** |

**The breadcrumb loop decides the whole question.** `BreadcrumbResolver.cs:121` calls the row rule inside a `foreach` over every referenced document at every level. A 50-row list page with two reference attributes resolves ~100 referenced documents → **~100 hook invocations**. Sync, that's free. Async-with-one-Raven-request, that's ~100 requests on a 30-cap session → **it breaks on the first page load**. Memoization is not an optimization here; it is the difference between shipping and not.

### 2.2 Two pre-existing cap bugs, independent of the hook

- **Streaming already blows the cap today (CRITICAL).** `StreamingQueryExecutor.cs:76` opens **one** session before the `await foreach` and reuses it for every batch — no per-batch reset, no `IgnoreMaxRequests`. Per batch it spends a `LoadAsync` in `FilterAsync` (projection case), one per breadcrumb level, one in `RedactAsync` — ~2–4 requests per batch on a session that never resets. A projection-backed stream over a referenced/row-scoped type **dies around batch 8–15, with the hook still synchronous.** It also pins every document the stream ever touched in the identity map (unbounded memory). A stream is long-lived by definition; the async hook worsens this but does not cause it.
- **Custom actions loop full detail reads per selected item (HIGH).** `ExecuteCustomAction.cs:113` calls `GetPersistentObjectAsync` per selected item on one shared session → ~5 selected items already ≈ 30 requests. Pre-existing.

### 2.3 `IgnoreMaxRequests` around the hook does **not** work

Raven's request counter is cumulative. `IgnoreMaxRequests` (`Extensions/SessionExtensions.cs:74,82`) raises the *ceiling* for a scope then restores it — `NumberOfRequests` keeps its accumulated value. Wrapping the hook lets the hook succeed and then throws on the framework's *next* request. It moves the exception; it does not buy headroom. Ruled out as a hook-level measure. (Uncapping a whole **stream** session is different and valid — see §3.4.)

## 3. Design

### 3.1 Async hook (breaking rename) — the approved direction

```csharp
// Actions/DefaultPersistentObjectActions.cs — REPLACES GetRowFilter(string), no backward compat
[NoInterfaceMember]
public virtual Task<Expression<Func<T, bool>>?> GetRowFilterAsync(string action)
    => Task.FromResult<Expression<Func<T, bool>>?>(null);
```

Only *construction* becomes async; the returned `Expression<Func<T,bool>>` is unchanged and still RavenDB-translatable, so pushdown, the derivation rule, projection fallback, and WITH CHECK are behavior-preserving. `Task`, not `ValueTask` (the hook is invoked reflectively; `Task<T> : Task` reuses the await-and-unwrap pattern `RowSecurity` already uses). Breaking, not additive: the additive alternative has a fail-open trap (a consumer overriding only the old sync member → `IsOverridden` false → row security silently off). The framework is in preview; the break is loud (compile error). Full mechanical map: §5 of the plan. This half is #239 as approved.

### 3.2 Per-request memo keyed by `(entityType, action)` — the cap mechanism (mandatory, same PR)

Add to the scoped `RowSecurity` a per-request memo consulted by `InvokeGetRowFilterAsync`:

- **Key:** `(Type entityType, string action)`. **Value:** the `Task<LambdaExpression?>` (store the *task*, not the result, so concurrent awaiters share one invocation), plus the compiled delegate alongside so `ResolveEffectiveRule` stops recompiling per call.
- **Effect:** invocations become bounded by **distinct (type, action) pairs touched per request** — a number fixed by the *model*, never by row count, page size, or batch count. The ~100-invocation list page collapses to `2 + (distinct referenced types)`; streaming collapses to one invocation per connection. This is the property that gives the severe margin: *no data-dependent growth.*
- **Correct by construction under the existing contract.** `DefaultPersistentObjectActions.cs:131` already declares the expression "evaluated per request: capture request-scoped data as constants" — the framework already promises request-stability, so memoizing only stops re-deriving something already declared invariant.
- **System-context short-circuit stays before the memo** (`RowSecurity.cs:193,361`).

Why `(type, action)` and **not** entity-type-only: `action` is a documented vocabulary and the `Can{Edit,Delete}` block (`DatabaseAccess.cs:120-127`) exists precisely to ask different actions and get different answers. Caching across actions turns a consumer whose Delete rule is stricter than their Read rule into a **call-order-dependent privilege escalation**. Keying on the action costs one path 3→1 and is not worth a fail-open edge. (This is why #239's acceptance criterion "one invocation per detail read" is **withdrawn** — a detail read is legitimately 3 distinct actions; the correct guarantee is *model-bounded, data-independent*, not *one*.)

### 3.3 Memo invalidation on the streaming re-auth tick — security, non-negotiable

A scoped `RowSecurity` on a socket means one invocation per **connection** — so a caller whose allow-list shrinks would keep seeing revoked rows until they disconnect. `StreamingQueryExecutor.cs:98-103` already re-checks type-level authz every 10 batches; the row-filter memo must be **cleared on the same tick** (a new `IRowSecurity.ResetRequestFilterCache()` the streaming loop calls). "Cached forever" becomes "at most 10 batches stale", matching the bound the type-level re-check already accepts, at one hook invocation per 10 batches. Without this, the memo trades a liveness bug for a security bug.

### 3.4 Fix the streaming session (pre-existing bug, same PR)

Open a short-lived **framework** session *inside* the batch loop for the framework's own work — `FilterAsync` (`:109`), `breadcrumbResolver.ResolveAsync` (`:119`), `RedactAsync` (`:124`) — instead of the connection session. Each batch starts from zero requests; the identity map is bounded to one batch (fixing the memory leak too); the cap regains meaning as a per-batch N+1 alarm. Do **not** replace the *consumer's* `args.Session` — their `IAsyncEnumerable` holds it across the whole enumeration — but **uncap that consumer stream session** (`MaxNumberOfRequestsPerSession = int.MaxValue`, reasoned in a comment): a socket open for an hour is not "a request", so the 30-cap's N+1-alarm purpose doesn't apply to it. The framework's own per-batch session stays at 30.

### 3.5 Fix the custom-action per-item loop (pre-existing bug, same PR)

`ExecuteCustomAction.cs:113` — replace the per-selected-item sequential `GetPersistentObjectAsync` loop with a batched row-gated load (or wrap in a deliberately-reasoned `IgnoreMaxRequests`), so S selected items don't linearly consume the 30-cap.

### 3.6 Diagnostic counter (defense-in-depth)

Count hook invocations per request in `RowSecurity`; warn above a threshold (~20), naming the types — same spirit as the one-shot `announced` logging (`:211,221`). The memo bounds *today's* call graph; the counter notices when someone adds the next `BreadcrumbResolver.cs:121`-shaped loop a year from now.

### 3.7 Rejected options (with reasons)

| Option | Verdict |
|---|---|
| Memo by `entityType` only | **Rejected** — call-order-dependent privilege escalation when Delete ≠ Read (§3.2). |
| Context-split (`GetRowFilterContextAsync` + sync `GetRowFilter(action, ctx)`) | **Rejected** — buys 8→5 (same order as the memo's 8; both model-bounded), for a two-method API + untyped context threaded through three call paths. Wrong trade. |
| Dedicated session handed to the hook | **Rejected** — a session parameter the hook may ignore is guidance in a parameter's clothes, and does nothing for an HTTP-calling hook. Not by-construction. |
| `IgnoreMaxRequests` around the hook | **Rejected** — cumulative counter; moves the exception, no headroom (§2.3). |
| Raise the global cap | **Rejected** — the cap is the N+1 alarm this feature's failure mode would trip; raising it globally deletes the alarm. (Uncapping the *stream* session specifically is accepted — §3.4.) |

## 4. Invariants that must survive (from #239 §3.3)

1. System-context short-circuit **before** the hook (`ComposeRowFilter:193`, `ResolveEffectiveRule:361`, `RedactAsync:256`, `EnsureRowSaveAllowedAsync:77`).
2. Constant predicates are **not** pushed into RQL (`ComposeRowFilter:203` — `filter.Body is ConstantExpression` returns the queryable untouched; enforcement falls to `FilterAsync`'s compiled in-memory predicate).
3. Compose ordering: after the projection decision, before materialization — an `await` there suspends nothing problematic (the queryable is an unexecuted expression tree).
4. Projection fallback + first-use logging (`announced`) unchanged.
5. 404-on-denial + the derivation rule (filter-only ⇒ compiled single-row check; both ⇒ AND) unchanged — this PR changes *how the expression is obtained*, not what it means.

## 5. Consumer contract (documented in `guide-row-security.md` + the hook doc block)

- The framework invokes the hook **at most once per (entity type, action) per request**, and caches the result — awaiting I/O inside it is expected and safe. **Stated as a promise**, so it's a contract, not a hope.
- On a stream the cache refreshes every ~10 batches; a filter is at most that stale.
- Because the result is cached per request, **the filter must be a pure function of request-scoped state** — don't return a filter depending on something you mutate later in the same request.
- Recommended (not required): back the hook's data source with a scoped service that caches its own lookup per scope (idiomatic DI; a free second bound).
- The sharp edge, named explicitly: **`IsAllowedAsync` is genuinely per-row and is NOT memoized.** If that hook does I/O it is an N+1 by construction — express the rule as a `GetRowFilterAsync` expression instead.

## 6. Acceptance criteria

1. `GetRowFilterAsync` is the only filter hook; the solution compiles with zero `.GetAwaiter().GetResult()`/`.Result` in any row-security path.
2. **Hook invocations per request are bounded by distinct `(type, action)` pairs — provably independent of row count / page size / batch count** (replaces #239's withdrawn "once per detail read"). Pinned by: a list-page test with N referenced documents asserting the hook resolves once per referenced *type*, not once per document; and a streaming test asserting one resolution per connection between re-auth ticks.
3. Streaming: a projection-backed, referenced-type stream survives ≫30 batches (per-batch framework session); a counting fixture proves per-batch framework requests don't accumulate across batches.
4. Memo staleness: a stream whose allow-list shrinks stops delivering the revoked rows within one re-auth tick (pinned by a test).
5. Custom action with many selected items does not hit the cap.
6. WebhooksDemo's org rule runs as an async expression filter (pushdown + WITH CHECK), closing `docs/issue_236_plan.md:604`.
7. Invariants §4 pinned (system-context exemption test + constant-predicate-not-pushed test still pass unmodified in meaning).
8. Docs updated; version bump `.NET → 10.0.0-preview.45` in lockstep (ng-spark untouched — no client-visible change).
