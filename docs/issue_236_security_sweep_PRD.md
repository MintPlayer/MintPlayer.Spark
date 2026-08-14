# PRD — Security sweep: bind document identity to the authorized entity type

- **Branch:** `feat/issue-236-row-level-security` (same branch as #236 — this sweep was surfaced by that work)
- **Origin:** After completing the #236 row-level-security milestones, a two-agent adversarial audit (2026-08-14) probed untrusted-input **write** paths. Both auditors converged on a single root cause and **empirically verified** the exploits against a real endpoint pipeline (`SparkEndpointFactory` + embedded RavenDB), not code reading alone.
- **Status:** **Implemented (all findings fixed in PR #237, CI green)** 2026-08-14. Findings below carry `file:line` evidence and, where marked, a verified exploit transcript; see the as-built table in [issue_236_security_sweep_plan.md](./issue_236_security_sweep_plan.md) for the commit + fix mapping.

## Root cause (one sentence)

**Document identity is never bound to the entity type that was authorized.** The generic persistent-object endpoints resolve the CLR type from `objectTypeId` (which *is* run through `IPermissionService`), but take the document **id** from the route or request body and trust it. RavenDB's `session.LoadAsync<T>(id)` does **not** enforce `@collection` — handed an id from another collection, it silently deserializes that document into `T`. So an authorization decision made about *type A* is applied to a write against a *document of type B*.

Every Critical finding below is a different surface of this one failure. The fix is correspondingly single: **at each point where a client-supplied id is loaded/stored/deleted, verify the document's collection matches the authorized type; on mismatch, 404** (indistinguishable from not-found, matching the existing M-3 uniformity rule for row-level denials).

## Threat model

An **authenticated** caller holding legitimate rights on *some* entity type (even a low-privilege one — a lookup table, a `Note`) is the attacker. `security.json` is trusted (operator-configured); the RavenDB store is trusted. The untrusted surface is the HTTP request: route values, the `PersistentObject` body (`Id`, `ObjectTypeId`, `Attributes`), custom-action `Parent`/`SelectedItems`, reference `refId`s, and sync-action `documentId`/`collection`/`data`.

---

## C1 — Cross-collection type confusion on GET / PUT / DELETE  *(Critical, verified)*

The plain PO endpoints authorize the **route** type but load/write/delete by an **untrusted id**.

- `Endpoints/PersistentObject/Update.cs:30-42,61` — type from `objectTypeId`, id from `{**id}`, unvalidated.
- `Services/DatabaseAccess.cs:79-112` — authorizes `"Read"` on the route type (`:84`), loads by id (`:91`); nothing compares the loaded document's `@collection`.
- `Actions/DefaultPersistentObjectActions.cs:25-26` — `OnLoadAsync` is a bare `session.LoadAsync<T>(id)`.
- `Services/DatabaseAccess.cs:184-192` — `EnsureSaveAuthorizedAsync` checks the route type's name only; the victim type is never consulted.
- `Endpoints/PersistentObject/Delete.cs:52-60` → `DatabaseAccess.cs:255-283` → `DefaultPersistentObjectActions.cs:90-99` — same shape for delete.

**Verified exploit** — caller holds rights on `Note`; `VictimUsers` is not even in the model:
```
GET    /spark/po/{NoteTypeId}/VictimUsers/3-A  -> 200   "name":"Roles","value":["User"]
DELETE /spark/po/{NoteTypeId}/VictimUsers/2-A  -> 204   document GONE
PUT    /spark/po/{NoteTypeId}/VictimUsers/1-A  -> 200
  BEFORE {"UserName":"admin","PasswordHash":"…secret","Roles":["User"], @collection: VictimUsers}
  AFTER  {"Title":"owned","Roles":["Administrator"],"UserName":"admin","PasswordHash":"…secret", @collection: VictimUsers}
```
The document keeps its collection and unknown fields (RavenDB's blittable merge), so this is a **surgical field overwrite that leaves a still-valid victim document** — reaching `SparkUser.Roles` (`Authorization/Identity/SparkUser.cs:35` → `UserStore.GetRolesAsync` → role claims → `security.json` groups) is privilege escalation to any group.

## C2 — Reference attributes read/exfiltrate any document  *(Critical, verified)*

`Services/EntityMapper.cs:600-629` resolves a client-supplied `refId` via `LoadReferenceAsync` (`:809-826`), a bare `session.LoadAsync<targetType>(refId)` — no permission, collection, or row check. The loaded entity is assigned to the property and persisted inside the attacker's own document, then returned on read.
```
PUT /spark/po/{NoteTypeId}/Notes/mine {"Name":"Author","DataType":"Reference","Value":"VictimUsers/4-A"} -> 200
GET /spark/po/{NoteTypeId}/Notes/mine  ->  "value":{"id":"VictimUsers/4-A","userName":"admin","passwordHash":"…secret"}
```
Any reference type with a `Value`/`Secret`/`Hash`/`Token`-shaped property is a general-purpose reader of the entire database.

## C3 — Custom-action `Parent` is gated against a client-chosen type  *(Critical, verified — regression in #236 M3)*

`Endpoints/Actions/ExecuteCustomAction.cs:83` loads `parent` with `submittedParent.ObjectTypeId` **from the request body**, while `SelectedItems` (`:97`) correctly uses the route's `entityType.Id`. So the M3 row-gate runs against the wrong type. `PersistentObject.ObjectTypeId` (`Abstractions/PersistentObject.cs:11`) is a plain wire-settable property.

**Verified Fleet exploit:** a Fleet manager granted `CarCopy/Car` + `QueryRead/Person` (and `Person` has no row rule) posts `{ "parent": { "objectTypeId": "<Person type>", "id": "cars/1-A" } }`. The type gate asks `CarCopy/Car` (granted); the load asks `Read/Person` with Person's (absent) row rule and returns a PO carrying `Id = "cars/1-A"`; `CarCopyAction` then `GetDocumentUncheckedAsync<Car>("cars/1-A")` — a car the caller may not see. This is a hole the M3 work itself introduced (`Parent` should have been pinned to the route type like `SelectedItems`).

## H1 — Sync `documentId` never checked against `collection`  *(High)*

`Services/SyncActionHandler.cs:39,107` assigns `po.Id = documentId` verbatim; `DatabaseAccess.cs:207,244` uses the type from the *collection* only. A module holding `Edit/Car` posts to `/spark/sync/apply` with `collection: "Cars"`, `documentId: "SparkUsers/1-A"` → `LoadAsync<Car>("SparkUsers/1-A")` deserializes the identity doc as a `Car`, merges, writes back — obliterating `PasswordHash`/roles. Delete variant deletes any document. Collection-level containment (which *collections* a module may touch) is sound; document-level (which *documents* within it) is not. **Note:** #236 M2 made module principals row-exempt (correct per D3), so type-level rights are now the *only* gate on a module — which is exactly why this missing id check matters more than before.

## H2 — Natural-id create silently overwrites an existing document  *(High)*

`SparkDocumentStoreConventions.cs:24-30` derives the id from entity contents for `IHasNaturalId`; `Create.cs:58` forces `Id = null` so `SavePersistentObjectAsync` takes the "New" branch — the row-level **Edit** gate and the etag concurrency check (both inside `if (!string.IsNullOrEmpty(Id))`, `DatabaseAccess.cs:213-230`) are skipped. `New` right alone rewrites any existing document of that type, with a 201 that looks like a normal create. No entity implements `IHasNaturalId` in-repo today, so this is a framework-level exposure, not a live demo one.

## M1 — Mass assignment into undeclared AsDetail child types  *(Medium)*

The top-level write gate is sound (`EntityMapper.cs:549-550` skips undeclared attributes), but `GetSchemaAttributeMap` returns `null` for a CLR type with no `EntityTypeDefinition` and `IsWritableBySchema` treats a null map as **allow-all** (`:548`). `WriteAsDetailAsync` (`:641-684`) derives the child type from the *property* and recurses — so an AsDetail child type absent from the model has no gate; any writable CLR property is client-settable by name.

## M2 — Custom actions write through `*Unchecked` APIs, skipping every write gate  *(Medium)*

`SaveDocumentUncheckedAsync` (`DatabaseAccess.cs:45-62`) bypasses `EnsureAuthorizedAsync`, row security, and the whole `OnSaveAsync` pipeline (so M2's WITH CHECK and ownership stamping never run). Both shipped demo actions use it with an id from `args.Parent` (`CarCopyAction.cs:22,34`, `SyncColumnsAction.cs:20,27`) — the template every consumer copies. Independent of C3, `CarCopyAction` creates a `Car` with `CreatedBy` unset (an orphan row no user's filter matches).

## M3 — Custom-action execution ignores `customActions.json`  *(Medium)*

`ExecuteCustomAction.cs:58` resolves from an AppDomain-wide scan (`CustomActionResolver.cs:55-86`); the config loader is consulted by the *list* endpoint (`ListCustomActions.cs:35-38`) but **not** by execute. Any `ICustomAction` in any loaded assembly is invocable by name, and `SelectionRule` is advisory. Contained by exact-match `security.json` rights (still needs `{Action}/{Type}`), but "removed from config" reads as "still callable".

## M4 — Lookup-reference reads are unauthenticated  *(Medium)*

`Endpoints/LookupReferences/List.cs` and `Get.cs` inject no `IPermissionService` (the mutating siblings do). Spark endpoints are anonymous at the ASP.NET layer by design (`SparkModuleRegistry.cs:18-20`), so with no in-handler check these are public: `GET /spark/lookupref/` enumerates every reference; `GET /spark/lookupref/{name}` dumps every value, and transient lookups reflect **all** public properties into `Extra` (`LookupReferenceService.cs:274-308`). Often customer names, site codes, cost centres.

## L1 — Streaming connection authorized once, never revalidated  *(Low)*

`StreamingQueryExecutor.cs:51` checks `EnsureAuthorizedAsync` once before the batch loop; the socket then lives on its handshake `ClaimsPrincipal`. Token expiry, group removal, or logout don't narrow or close it. *(The per-batch row filter + M4 redaction **are** correctly applied to every batch — auditor verified `:91,:106`.)*

## L2 — `ProgramUnits/Get.cs` fails open  *(Low)*

`:88-91` bare `catch {}` returns an empty map ("all items shown"); `:36` includes an unresolvable unit. Anonymous. Leaks navigation structure + entity/query names to an unauthenticated caller. Click-through still hits checked endpoints, so metadata-only.

## Already resolved on this branch (do not re-fix)

- **Fail-open `IsSystemContext`** (auditor F3, rated High): the actions/sync auditor read commit `46b7bbd` (pre-M5). #236 **M5 (`df18e19`) already fixed this** — exemption is now positive-claim-only, fails closed. The write-paths auditor (working-tree) confirmed the redaction write-back shield holds and did not re-flag it.

## Non-goals

- Rearchitecting authorization. The fix binds id↔type at the existing chokepoints; it does not change `security.json` or the row-security model.
- The `*Unchecked` API's existence — it's a legitimate escape hatch; M2's remedy is giving custom actions a *checked* default and fixing the demos, not removing it.

## Acceptance

A caller holding rights on type A cannot read, overwrite, or delete a document of type B by naming B's id on any endpoint (PO get/update/delete, reference attribute, custom-action parent, sync). Verified by regression tests mirroring the auditors' transcripts.
