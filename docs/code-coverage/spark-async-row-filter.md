# Spark handoff — async row filter (`GetRowFilterAsync`)

Status: **✅ SHIPPED** — filed as
[Spark#239](https://github.com/MintPlayer/MintPlayer.Spark/issues/239), implemented by
[PR #240](https://github.com/MintPlayer/MintPlayer.Spark/pull/240) in
`MintPlayer.Spark 10.0.0-preview.45` and adopted here (Coverage's Actions classes are async-first).
Kept for the record: it documents why the hook had to be async and the invariants the
implementation had to preserve. Live status of the whole adoption is in
[adopt-spark-generic-ui.md](adopt-spark-generic-ui.md) — see its upstream scoreboard. This doc records Coverage's consumer requirement and what the change means
for the adoption plan in [adopt-spark-generic-ui.md](adopt-spark-generic-ui.md).

> Research basis: a two-agent investigation (2026-08-15) of MintPlayer.Spark `origin/master`
> (`5d5fce0`, preview.44) — full change-surface map and execution-context audit. The complete
> PRD/plan lives in the upstream issue; this is the Coverage-side summary.

## Why Coverage needs it

`DefaultPersistentObjectActions<T>.GetRowFilter(string action)` is the **only synchronous virtual
hook** on the Actions class — and Coverage's row rules need async data:

- **Repository**: the allowed-owners list comes from `IGitHubAccessService.GetAllowedOwnersAsync()`
  — `UserManager.GetUserAsync` (RavenDB), the token service, and on cache miss a GitHub HTTP call
  (`GitHubAccessService.cs:30-88`, 5-min memory cache).
- **Commit**: the pushdown-capable filter is `c => c.Repository.In(visibleRepoIds)`, where
  `visibleRepoIds` requires a RavenDB query over `Repository` — async on `IAsyncDocumentSession`.

With a sync hook the only bridge is `.GetAwaiter().GetResult()` — sync-over-async on the hot
request path, blocking a thread on identity-store + GitHub I/O on every cold cache. The upstream
issue requests the hook become `Task<Expression<Func<T,bool>>?> GetRowFilterAsync(string action)`
(breaking rename, sync member deleted), with per-request memoization since the hook runs 2–3× per
request and once per batch on streams.

## Impact on the adoption plan

- **M2 (open `/spark` reads) waits for the preview that carries `GetRowFilterAsync`** rather than
  shipping sync-over-async bridges that would be rewritten immediately. The Actions classes will
  be written async-first.
- **M3 (coverage-bar attribute renderer) is client-only and proceeds independently.**
- Already committed on the `adopt-spark-generic-ui` branch: the PRD/plan doc and the
  preview.44 / ng-spark 22.0.9 / ng-bootstrap 22.16.0 pin bumps. A later preview bump for the
  async hook will be a one-line follow-up.

## Design essentials (agreed by the investigation, argued in full in the issue)

- **Breaking rename** to `GetRowFilterAsync`, `Task<...>` not `ValueTask` (reuses the reflective
  await-and-unwrap pattern `RowSecurity` already uses for `IsAllowedAsync` and
  `GetProtectedAttributesAsync`). House precedent: `OnQueryAsync` was deleted outright in M5; a
  two-member transition risks a fail-open override-detection bug.
- Everything downstream is already async: `ComposeRowFilter` → `ComposeRowFilterAsync` (3 call
  sites), `ResolveEffectiveRule` → async, `EnsureRowSaveAllowedAsync` awaits — no sync caller
  exists anywhere, no startup diagnostic exists (mode logging is lazy per-request), nothing caches
  the expression across requests.
- Invariants to preserve: system-context short-circuit **before** invoking the hook; constant
  predicates (`x => false`) not pushed into RQL; compose after the projection decision, before
  materialization.
- Per-request memo keyed `(entityType, action)` in scoped `RowSecurity` — collapses the detail
  path's 3 invocations and freezes a stream's filter at connect time (consistent with the already
  frozen streaming principal).
