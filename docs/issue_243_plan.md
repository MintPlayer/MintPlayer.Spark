# Plan — Issue #243: Intersect the per-row `can` block with type-level rights

**PRD:** `docs/issue_243_PRD.md` · **Branch:** `feat/issue-243-can-block-type-rights` · one commit per milestone.

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

1. **Permissive direction (the #243 bug):** row-ruled type + an access-control double that
   allows Read/Query and denies Edit/Delete ⇒ `Can` present with `edit:false, delete:false`.
   Precedent for overriding authz in the endpoint factory: `configureServices` +
   `services.RemoveAll<...>()` (see `GetProgramUnitsEndpointTests.cs:53`); replace
   `IAccessControl` with an NSubstitute double rather than `IPermissionService` so the real
   `PermissionService` action-string composition (`"Edit/{TypeName}"`) stays under test.
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
- `libs/spark/MintPlayer.Spark/README.md` — only if it describes the can block; align wording.

## M3 — Verification sweep + PR

- Run `DatabaseAccessRowLevelAuthzTests` + `PermissionServiceDefaultsTests` +
  `RowLevelQueryAuthorizationTests` (named classes, isolated — full suite is flaky under load).
- ng-spark Vitest for po-detail (comment-only change; specs must stay green as-is).
- Open PR referencing #243; call out: single-site server fix, no client logic change, zero
  RavenDB-request-cap impact, no version bump until merge is prepared (follow repo release flow).

## Explicitly not doing

Per PRD: no per-row `can` on lists, no client-side intersection, no dist/types hand-edit,
no `canUpdate`→`canEdit` spec-mock sweep.
