# PRD — Issue #243: Per-row `can` block must respect type-level rights

**Status:** Proposed
**Issue:** [#243](https://github.com/MintPlayer/MintPlayer.Spark/issues/243)
**Branch:** `feat/issue-243-can-block-type-rights`
**Depends on:** #236/#237 (row-level security, preview.44), #239/#240 (async row filter, preview.45)

## Problem

The per-row `can: { edit, delete }` block (#236 G5) is computed **from the row rule alone**
(`IRowSecurity.IsAllowedAsync`) and never intersected with the caller's **type-level** rights
(`IPermissionService`). On a type whose rows are broadly readable but whose `security.json`
grants only `QueryRead` — the documented anonymous-read pattern — every readable row comes back
with `can: { edit: true, delete: true }`, **including for anonymous callers**.

`spark-po-detail` prefers `item.can` over `GET /spark/permissions/{type}` (a strict override,
by design: a row rule must be able to *deny* what type-level allows), so Edit/Delete buttons
render for viewers who hold no write right at all. Clicking through fails server-side —
`SavePersistentObjectAsync`/`DeletePersistentObjectAsync` still run `EnsureAuthorizedAsync`
before the row gate — so this is a **UI overclaim, not a write hole**. But the default
experience of the documented read-only pattern is broken-looking buttons.

Observed live on Coverage (preview.45): `QueryRead/Repository` for `Everyone`, a visibility
row filter, anonymous `GET /spark/po/Repository/{id}` → `"can": { "edit": true, "delete": true }`.

## Root cause

`DatabaseAccess.GetPersistentObjectAsync` (`libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs:117-127`):

```csharp
if (rowSecurity.HasRowRule(entityType))
{
    persistentObject.Can = new PersistentObjectPermissions
    {
        Edit = await rowSecurity.IsAllowedAsync(entityType, "Edit", entity),
        Delete = await rowSecurity.IsAllowedAsync(entityType, "Delete", entity),
    };
}
```

`rowSecurity.IsAllowedAsync` answers "does the row rule allow this action on this row?" — for a
type whose `GetRowFilterAsync` only scopes *visibility* (returns the same filter for every
action, or null for write actions), the answer for Edit/Delete on a visible row is `true`.
The type-level right is never consulted. The invariant the client relies on — *`can` never
claims more than `/spark/permissions/{type}` would* — is not enforced anywhere.

## Investigation findings (two-agent sweep, 2026-08-15)

- **Single producer.** The block above is the ONLY place `Can` is assigned in the repo. The
  list path (`GetPersistentObjectsAsync`), `QueryExecutor`, streaming, and all endpoints never
  set it. Single-site fix.
- **Single consumer.** `spark-po-detail.component.ts:104-109` is the only reader of `can` across
  ng-spark, ng-spark-auth, and every demo ClientApp:
  `this.canEdit.set(can ? can.edit : permissions.canEdit)`. Once the server bounds `can` by
  type-level rights, that override is exactly equivalent to an intersection — **no client logic
  change is needed**. Nothing ORs the two values anywhere.
- **Mirror the permissions endpoint.** `GET /spark/permissions/{type}`
  (`Endpoints/Permissions/GetPermissions.cs:25-31`) computes
  `IsAllowedAsync("Edit"/"Delete", entityType.Name)`. The fix must use the same calls with the
  same `entityTypeDefinition.Name` target so the two surfaces can never disagree in the
  permissive direction.
- **Zero request-cap impact.** `IPermissionService.IsAllowedAsync` does no RavenDB or HTTP I/O:
  groups come from claims (`ClaimsGroupMembershipProvider`), rights from the memory-cached
  `SecurityConfigurationLoader` (singleton, 5-min cache). Two extra in-memory calls on a
  single-row detail read are negligible. (Guardrail for the future: `IGroupMembershipProvider`
  is async and replaceable with a DB lookup, so type-level checks must stay *out* of per-row
  loops — hoist them if the list path ever grows a `can` block.)
- **No system-context exemption needed — adding one would be wrong.** `SparkSystemContext`
  exempts ROW security only; its contract says type-level authz still governs which types a
  module may touch. For a system principal the row rule resolves to null (allow), so after the
  fix its `can` block simply carries the type-level answer — correct.
- **Default access control is deny-all.** Without `AddAuthorization()` or
  `AllowAnonymousAccess()`, `DenyAllAccessControl` refuses everything — but such a caller never
  reaches the can block (the `EnsureAuthorizedAsync("Read", …)` at the top of
  `GetPersistentObjectAsync` throws first). Test factories use `AllowAnonymousAccess()`
  (allow-all), so the existing can-block test survives unchanged.

## Fix

Intersect each per-row affordance with the caller's type-level right:

```csharp
if (rowSecurity.HasRowRule(entityType))
{
    persistentObject.Can = new PersistentObjectPermissions
    {
        Edit = await permissionService.IsAllowedAsync("Edit", entityTypeDefinition.Name)
            && await rowSecurity.IsAllowedAsync(entityType, "Edit", entity),
        Delete = await permissionService.IsAllowedAsync("Delete", entityTypeDefinition.Name)
            && await rowSecurity.IsAllowedAsync(entityType, "Delete", entity),
    };
}
```

(Short-circuit order: type-level first — it's the cheap in-memory check and skipping the row
hook when the type already denies avoids invoking consumer code needlessly. Both
`permissionService` and `rowSecurity` are already injected on `DatabaseAccess`; no ctor or
source-generator changes.)

## Decisions

- **D1 — Intersect, keep the block present.** Alternative considered: omit `can` entirely when
  type-level denies both. Rejected: `can: {edit:false, delete:false}` is the truthful answer,
  and omitting it would make the client fall back to type-level — equivalent today but a more
  fragile contract ("absent" would then mean two different things).
- **D2 — No client-side intersection.** The server is authoritative; ng-spark keeps its
  `can ? can.x : permissions.x` override (same bias as custom-action authz: clients don't
  re-check). A client intersection could only mask a future server bug. Instead, record the new
  invariant in the doc comments on both sides.
- **D3 — List path stays out of scope.** Query/list responses carry no per-row `can`; grids
  gate on type-level permissions only. Extending `can` to lists is a separate feature
  (per-row row-hook calls + hoisted type-level checks); not needed to fix the overclaim.
- **D4 — "New" is not part of the block.** `PersistentObjectPermissions` has no `Create`;
  creation affordances remain type-level (`canCreate` from the permissions endpoint). Unchanged.
- **D5 — Fix the `canUpdate` spec-mock drift; `canEdit` is the name.** Three ng-spark spec
  mocks stub `getPermissions` with `canUpdate` instead of `canEdit`. `canEdit` is what the wire
  contract serializes (`GetPermissions.cs`), what `EntityPermissions` declares, and what matches
  the framework-wide "Edit" action vocabulary (`security.json`, `PersistentObjectPermissions.Edit`,
  the po-detail `canEdit` signal) — the mocks are simply wrong and would mislead the next test
  that reads `canEdit`. Rename in the mocks; no production code uses `canUpdate` anywhere.

## Acceptance criteria

1. `QueryRead`-only grants + a row rule ⇒ `can` comes back `{ edit: false, delete: false }`
   for every caller, including anonymous. Pinned by a test combining an `IAccessControl`
   double (Read/Query allowed, Edit/Delete denied) with a row-ruled Actions class — the
   **permissive-direction** case that was never covered.
2. A caller WITH type-level Edit but a row rule denying this row still gets
   `can.edit = false` — the existing restrictive direction must not regress (pinned today by
   `DatabaseAccessRowLevelAuthzTests.Get_attaches_the_per_row_can_block_for_a_row_scoped_type`
   and the ng-spark spec `'prefers the per-row can block over type-level permissions'`).
3. A type WITHOUT a row rule still returns `can = null` (client falls back to type-level) —
   currently asserted only in a comment; pin it.
4. `can` never claims more than `GET /spark/permissions/{type}`: documented as the invariant on
   `PersistentObject.Can`'s XML doc and in the ng-spark model/component comments.

## Out of scope

- Per-row `can` on list/query responses (D3).
- Client-side defense-in-depth intersection (D2).
- The stale checked-in `libs/node_packages/ng-spark/dist/types` (missing `can`) — dist is
  publish-output; regenerated by the release pipeline, not hand-edited.
