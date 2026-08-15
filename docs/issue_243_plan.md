# Plan — Issue #243: Intersect the per-row `can` block with type-level rights

**PRD:** `docs/issue_243_PRD.md` · **Branch:** `feat/issue-243-can-block-type-rights` · one commit per milestone.

> **Status: all milestones DONE** — PR [#244](https://github.com/MintPlayer/MintPlayer.Spark/pull/244).
> M0 outcome: the red repro failed exactly as predicted (`can.edit == true` under a QueryRead-only
> `IAccessControl` double) and the `configureServices` override won without needing the
> `RemoveAll` fallback. Named suites 22/22, ng-spark Vitest 214/214.
> **Live verification on Fleet (2026-08-15):** throwaway `Viewers`-role user + a car it owns —
> API returned `can: {edit:false, delete:false}` (log: `Authorization DENIED for Edit/Car
> (groups: [Viewers])`), and the detail UI rendered no Edit/Delete buttons; test data cleaned up.

## M0 — Spike: red repro test (timeboxed, ~30 min)

One spike, run before any production change: write the permissive-direction test against
UNMODIFIED code and watch it fail with `can.edit == true`. It de-risks the only two real
unknowns at once — (a) that the `SparkEndpointFactory` `configureServices` hook can swap
`IAccessControl` for a Read/Query-only double while row rules stay active (the hook runs
after `AddSpark`/`AllowAnonymousAccess`, `SparkEndpointFactory.cs:92-101`, so the override
wins — expected to work, unproven for `IAccessControl` specifically), and (b) that the test
faithfully reproduces the Coverage overclaim. The spike artifact IS M1's regression test —
nothing is thrown away; flipping it green is the M1 fix.

If the override does NOT win (e.g. something resolves `IAccessControl` before the
replacement), fall back to `RemoveAll<IAccessControl>()` + re-register, per the
`GetProgramUnitsEndpointTests.cs:53` precedent — decide inside the timebox.
*(Outcome: not needed — the override won as-is.)*

No other spikes: the production change is two in-memory calls on services already injected
into `DatabaseAccess`, and the client change is comments + mock renames only.

## M1 — Server fix + regression tests

**Code** — `libs/spark/MintPlayer.Spark/Services/DatabaseAccess.cs` (~:120-127), inside
`GetPersistentObjectAsync`:

- `Can.Edit` = `await permissionService.IsAllowedAsync("Edit", entityTypeDefinition.Name) && await rowSecurity.IsAllowedAsync(entityType, "Edit", entity)`
- `Can.Delete` = same with `"Delete"`.
- Type-level check first (cheap, in-memory; skips invoking the consumer's row hook when the
  type already denies). No ctor/DI changes — both services are already `[Inject]`ed.
- Update the `#236 G5` comment above the block to state the invariant: *the block never claims
  more than `GET /spark/permissions/{type}`; the row rule can only narrow, never widen.*

**Tests** — `tests/MintPlayer.Spark.Tests/Services/DatabaseAccessRowLevelAuthzTests.cs`:

1. **Permissive direction (the #243 bug):** the M0 spike test, flipped green — row-ruled type
   + an `IAccessControl` double that allows Read/Query and denies Edit/Delete ⇒ `Can` present
   with `edit:false, delete:false`. Replace `IAccessControl` rather than `IPermissionService`
   so the real `PermissionService` action-string composition (`"Edit/{TypeName}"`) stays under
   test.
2. **Restrictive direction (no regression):** existing
   `Get_attaches_the_per_row_can_block_for_a_row_scoped_type` keeps passing unchanged
   (factory is allow-all ⇒ intersection is a no-op there).
3. **Unruled type ⇒ `Can` is null:** currently only claimed in a comment; pin it.

Verify by type-check/read-through per milestone; run the named test class at the end (M3),
not per-milestone.

## M2 — Documentation of the invariant

- `libs/spark/MintPlayer.Spark.Abstractions/PersistentObject.cs` — XML doc on `Can` /
  `PersistentObjectPermissions`: values are the intersection of type-level rights and the row
  rule; null means "no row rule, use `GET /spark/permissions/{type}`".
- `docs/guide-row-security.md` — the per-row `can` section: state the intersection, and remove
  any wording implying consumers must return `x => false` from `GetRowFilterAsync` for
  Edit/Delete on read-only surfaces (that Coverage workaround becomes unnecessary).
- ng-spark comment-only updates (no logic): `po-detail/src/spark-po-detail.component.ts:104-106`
  and `models/src/persistent-object.ts` doc block — record that the server bounds `can` by
  type-level rights, so the override is safe.
- Spec-mock naming drift (D5): rename `canUpdate` → `canEdit` in the three `getPermissions`
  mocks (`query-list/src/spark-query-list.component.spec.ts:68,118`,
  `po-form/src/spark-po-form.component.spec.ts:75`) so mocks match `EntityPermissions` and the
  wire contract. Test-only rename; Vitest must stay green.
- `libs/spark/MintPlayer.Spark/README.md` — only if it describes the can block; align wording.

## M3 — Verification sweep + PR

- Run `DatabaseAccessRowLevelAuthzTests` + `PermissionServiceDefaultsTests` +
  `RowLevelQueryAuthorizationTests` (named classes, isolated — full suite is flaky under load).
- ng-spark Vitest for po-detail (comment-only change; specs must stay green as-is).
- Open PR referencing #243; call out: single-site server fix, no client logic change, zero
  RavenDB-request-cap impact, no version bump until merge is prepared (follow repo release flow).

## Explicitly not doing

Per PRD: no per-row `can` on lists, no client-side intersection, no dist/types hand-edit.
