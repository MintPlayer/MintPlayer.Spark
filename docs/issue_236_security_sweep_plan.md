# Implementation plan — Security sweep (id↔type binding)

See [issue_236_security_sweep_PRD.md](./issue_236_security_sweep_PRD.md). Same branch as #236. One commit per milestone; full suites at the end.

The Critical findings share one root cause, so **S1 is the load-bearing fix** — a single collection-binding guard applied at every load-by-untrusted-id chokepoint. The rest are narrower.

## S1 — Bind document id to the authorized collection (C1, H1, and the load half of C2)  ★ core fix

**Mechanism.** A document's id prefix encodes its collection under the store's id conventions. Add one helper on the store conventions: given an entity CLR type and a candidate id, is that id in the type's collection? Two complementary checks (use both; the metadata check is authoritative):

1. **Prefix pre-check (cheap, before load):** compare the id's collection prefix to `documentStore.Conventions.GetCollectionName(entityType)` transformed by the id-prefix convention. Rejects the obvious cross-collection id without a round-trip.
2. **Metadata post-check (authoritative, after load):** `session.Advanced.GetMetadataFor(entity)[Constants.Documents.Metadata.Collection]` must equal the expected collection. Catches custom id conventions and confirms the loaded document really is of the authorized type.

On mismatch: treat as **not-found** (return null → 404 at the endpoint), never a distinct error — matches the M-3 uniformity the row gate already uses.

**Sites (all load/store/delete by a client-supplied id):**
- `Services/DatabaseAccess.cs` — `GetPersistentObjectAsync` (`:91` after the actions load), `SavePersistentObjectAsync` (the existing-entity load `:209` and again on the `OnSaveAsync` result before returning), `DeletePersistentObjectAsync` (`:262`), and `LoadEntityAsync`/`LoadEntityViaActionsAsync` helpers.
- `Actions/DefaultPersistentObjectActions.cs` — `OnSaveAsync` (`:35` existing load), `OnDeleteAsync` (`:61`). These are overridable; the authoritative guard must live at the `DatabaseAccess` chokepoint so an app that overrides `OnSaveAsync` can't opt out of it. Put the check in `DatabaseAccess` around the actions call, not only inside the default actions.
- `Services/SyncActionHandler.cs` — `HandleSaveAsync`/`HandleDeleteAsync`: reject when `documentId`'s collection ≠ the resolved `collection` (before touching `DatabaseAccess`), throwing `SparkSyncNotAuthorizableException`.

**Tests:** cross-collection GET/PUT/DELETE → 404, document untouched (mirror the auditor transcript with a registered victim type + an unregistered-by-model victim collection); sync write with mismatched `documentId`/`collection` → refused, victim intact; same-collection operations still succeed unchanged.

## S2 — Reference resolution goes through the authorized path (C2)  

`Services/EntityMapper.cs` `LoadReferenceAsync` (`:809-826`): the reference target's collection must match the declared reference-target type (the `[Reference(typeof(X))]` type is known from the schema), and — where a session/authorization context is available — the caller must hold `Read` on it and pass the row gate. Minimum viable: enforce the **collection match against the declared target type** (kills the arbitrary-document exfiltration); the read/row check is the defence-in-depth follow-up. A refId naming a document outside the declared target collection → reference left null (or reject), never a blind load.

**Tests:** a reference attribute whose `Value` names a foreign-collection id does not load that document; a valid same-target-collection ref still resolves.

## S3 — Custom-action `Parent` pinned to the route type (C3)  

`Endpoints/Actions/ExecuteCustomAction.cs:83`: load `parent` with `entityType.Id`, exactly as `SelectedItems` already does (`:97`) — ignore `submittedParent.ObjectTypeId` for the row-gated load (it remains on `SubmittedParent` for actions that legitimately read the raw wire value). With S1 in place the id is then also collection-checked. This closes the hole the #236 M3 work left.

**Tests:** a custom action whose `parent.objectTypeId` names a different type than the route → the parent load uses the route type and 404s on a foreign id; same-type parent still resolves.

## S4 — Natural-id create runs the update gates on collision (H2)  

`Services/DatabaseAccess.cs` `SavePersistentObjectAsync`: for an `IHasNaturalId` type, resolve the derived id before storing; if a document already exists at that id, route through the `Edit` right, the row-level Edit gate, and the concurrency check rather than the create path. (No in-repo consumer today, but it's a documented public feature.)

**Tests:** a second create with colliding natural-id contents is treated as an edit — refused without `Edit`/row rights, concurrency-checked with an etag.

## S5 — Fail closed on undeclared AsDetail child types (M1)  

`Services/EntityMapper.cs`: when an AsDetail child CLR type has no `EntityTypeDefinition` (`GetSchemaAttributeMap` null), write nothing rather than allow-all (`:548` inversion, scoped to the AsDetail-child path so the top-level behaviour — already safe — is unchanged). Consider the same inversion for the top-level null-map case behind a flag if any legitimate caller relies on it (verify none do first).

**Tests:** an AsDetail attribute naming an undeclared child type with attacker attributes writes none of them.

## S6 — Custom-action hardening (M2, M3)  

- Give custom actions a **checked** write path as the documented default (`SavePersistentObjectAsync` with the action's entity type); rewrite `CarCopyAction` and `SyncColumnsAction` to use it (they're the copy-paste template). *(Demo edits are optional per the user — but these two are the security template, so worth doing.)*
- `ExecuteCustomAction` looks the action up in `customActions.json` first and 404s if absent (mirror `ListCustomActions`); enforce `SelectionRule` server-side.

## S7 — Lookup-reference read authorization (M4)  

Add `EnsureAuthorizedAsync` to `Endpoints/LookupReferences/List.cs` and `Get.cs` (a `Query/LookupReferences` resource, or per-name `Read/LookupReference/{name}`), and filter the list to readable references.

## S8 — Lower-severity (L1, L2) — fold in or file follow-ups  

- Streaming: cap lifetime + periodic re-`EnsureAuthorizedAsync` with a credential-freshness check (L1).
- `ProgramUnits/Get.cs`: invert both fail-open defaults; log the swallowed exception (L2).

These two don't gate the PR; fold in if cheap, else file follow-up issues.

## Sequencing

S1 first and standalone-committed (it's the model change and the highest-value fix). S2–S4 close the remaining Criticals/Highs. S5–S7 are contained. S8 optional. Full `MintPlayer.Spark.Tests` + ng-spark Vitest sweep at the end; E2E security suite re-run to confirm the new regression tests and no environmental interference.

## Milestone → finding map

| Step | Closes | Severity |
|---|---|---|
| S1 | C1, H1, C2 (load half) | Critical/High |
| S2 | C2 (reference exfil) | Critical |
| S3 | C3 | Critical |
| S4 | H2 | High |
| S5 | M1 | Medium |
| S6 | M2, M3 | Medium |
| S7 | M4 | Medium |
| S8 | L1, L2 | Low |
