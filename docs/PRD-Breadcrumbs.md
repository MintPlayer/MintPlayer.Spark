# PRD — Recursive Breadcrumb Resolution for PersistentObjects & References

**Status:** Draft for review
**Date:** 2026-06-08
**Author:** Investigation team (4 parallel explorers) + synthesis
**Scope:** `MintPlayer.Spark` core, `MintPlayer.Spark.Abstractions`, `MintPlayer.Spark.SourceGenerators`, `@mintplayer/ng-spark` (presentation only)
**Backward compatibility:** Not required — framework is in preview. We may rename/replace existing shapes.

---

## 1. Problem statement

A developer must be able to declare, per entity type, a **breadcrumb template** composed of attribute names and literal text, e.g.:

```csharp
[Breadcrumb("{ParkedCar} ({Coordinates})")]
class ParkingSpot
{
    string Coordinates { get; set; }

    [Reference(typeof(Car))]
    string? ParkedCar { get; set; }
}

[Breadcrumb("{LicensePlate} ({Driver})")]
class Car
{
    string LicensePlate { get; set; }

    [Reference(typeof(Person))]
    string Driver { get; set; }
}

[Breadcrumb("{FirstName} {LastName}")]
class Person { string FirstName { get; set; } string LastName { get; set; } }
```

When a `ParkingSpot` breadcrumb is rendered, the `{ParkedCar}` placeholder (a **reference**) must render the **Car's** breadcrumb, whose `{Driver}` placeholder must in turn render the **Person's** breadcrumb — i.e. the breadcrumb resolves **recursively across reference boundaries, multiple levels deep**:

```
ParkingSpot:  {ParkedCar} ({Coordinates})
                  └─ Car:  {LicensePlate} ({Driver})
                                              └─ Person:  {FirstName} {LastName}
```

Scalar placeholders (`{Coordinates}`, `{LicensePlate}`) render their value. Reference placeholders render the referenced entity's breadcrumb.

The same reference rendering must be **identical** in all five surfaces:
1. query-list page,
2. po-detail page (single reference),
3. po-detail page (reference array → chips),
4. the sub-query viewer on the po-detail page (`SparkSubQueryComponent`),
5. the multi-select reference picker (`bs-tree-select`, added in `54d1c81`).

### Hard constraints

- **RavenDB 30-requests-per-session limit.** A naive depth-first, per-reference `LoadAsync` recursion explodes to `O(rows × fan-out × depth)` requests and will trip the limit.
- **Collection-vs-index split.** A breadcrumb field may live on the **collection document** (`FirstName`, `LastName`) or be **computed only in the index/projection** (`VPerson.FullName`, `VCar.OwnerFullName`). Computing the breadcrumb *inside the index* (the developer's first idea) was rejected because the value is then absent on the collection-backed PO-detail page. The breadcrumb must resolve consistently for **both** the projection-backed query list and the collection-backed PO page.
- **No silent wrong output.** Today an unresolved `{ParkedCar}` placeholder silently renders a raw id (`cars/1-A`). The new system must fail fast at model-sync time, not render garbage at runtime.

---

## 2. Current state (as investigated)

### 2.1 Backend
- `EntityTypeDefinition` carries `DisplayFormat` (template, `{Prop}` placeholders) and `DisplayAttribute` (single property fallback). There is **no `Breadcrumb` field**. (`EntityTypeDefinition.cs:31,35`)
- `EntityMapper.GetEntityDisplayName` → `ResolveDisplayFormat` replaces `{Prop}` with `entity.Prop?.ToString()`. **Flat substitution, no reference recursion** — a reference placeholder renders its raw id. (`EntityMapper.cs:1009-1025`)
- `EntityMapper.PopulateAttributeValues` sets `po.Breadcrumb = displayName` and, for reference attributes, sets `attr.Breadcrumb` / `attr.Breadcrumbs[id]` to the referenced entity's **display name** — but only **one level deep** and only from a preloaded `includedDocuments` dict. (`EntityMapper.cs:185-246`)
- `ReferenceResolver.ResolveReferencedDocumentsAsync` collects reference ids and loads each referenced document **one-by-one** in a nested loop (`foreach type → foreach id → session.LoadAsync<T>(id)`), applying row-level `Read` auth per doc. Saved from request blow-up **only** when `.Include()` primed the session cache during the list query. It does **not recurse** into the referenced entity's own references. (`ReferenceResolver.cs:99-150`)
- `.Include()` is applied to list/query queryables (`ApplyIncludes`, `ReferenceResolver.cs:74-97`) — first level only.
- Sessions keep RavenDB's default `MaxNumberOfRequestsPerSession = 30`; `SessionExtensions.IgnoreMaxRequests()` exists as an escape hatch. (`SparkMiddleware.cs:106-110`)
- Wire shape already supports it: `PersistentObject.Breadcrumb`, `PersistentObjectAttribute.Breadcrumb` (single), `PersistentObjectAttribute.Breadcrumbs` (`Dictionary<id,label>` for arrays). (`PersistentObject.cs`)

### 2.2 Frontend (`@mintplayer/ng-spark`)
- Already **presentation-only**: every surface reads server-computed strings (`po.breadcrumb`, `attr.breadcrumb`, `attr.breadcrumbs`). No client-side recursion exists or is wanted.
- query-list & po-detail render multi-references via the `referenceChips` pipe (`breadcrumbs[id] → label`); single references via the `attributeValue` pipe (`attr.breadcrumb`). po-detail adds `routerLink` via `referenceLinkRoute`.
- `SparkSubQueryComponent` has **no explicit multi-reference chip branch** — it falls through to `attributeValue`. (Minor inconsistency to align.)
- The multi-select picker builds `bs-tree-select` node labels from `po.breadcrumb || po.name || po.id`. It already benefits automatically once query results carry correct breadcrumbs.

### 2.3 The two real defects this PRD fixes
1. **No recursion.** Reference placeholders never expand to the referenced entity's breadcrumb.
2. **Unbatched, single-level loading** that risks the 30-request limit and cannot support recursion at all.

### 2.4 Consolidation mandate — exactly one breadcrumb/display system

Today the same "resolve a display/breadcrumb value" concern is implemented **multiple times** across three backend read paths and several frontend pipes (some with their *own* template engine and hardcoded property-name guessing). This is the root cause of drift between surfaces. **A non-negotiable goal of this work is that, when it lands, exactly one breadcrumb system exists** — server-side, in `BreadcrumbResolver`. Every duplicate below is **deleted** (not deprecated — preview, no back-compat) or reduced to a thin reader of the server-computed string.

**Backend — three read paths must funnel through `BreadcrumbResolver`; the old machinery is removed:**

| Location | Current code | Action |
|---|---|---|
| `Services/EntityMapper.cs:976-1025` | `GetEntityDisplayName`, `ResolveDisplayFormat`, and the inline single/array breadcrumb block in `PopulateAttributeValues` (`:185-246`) | **Delete.** EntityMapper only *reads* a precomputed `BreadcrumbResult` and copies strings to `po.Breadcrumb` / `attr.Breadcrumb` / `attr.Breadcrumbs`. |
| `Services/ReferenceResolver.cs:99-150` | `ResolveReferencedDocumentsAsync` (per-id, single-level loader + its own `Read`-auth loop) | **Remove from the breadcrumb path**; fold its referenced-doc loading into the resolver's BFS. Keep only `GetReferenceProperties` + `ApplyIncludes` (still used to prime the session cache). Extract the `Read`-auth helper to a shared `IRowSecurity`. |
| `Services/DatabaseAccess.cs` | Get/List build `includedDocuments` and pass them to `EntityMapper` | Replace with a `BreadcrumbResolver.ResolveAsync` call + map. |
| `Services/QueryExecutor.cs` | database + custom query mapping; server-side `search` reads `attr.Breadcrumb` | Route through resolver **after** pagination. |
| `Streaming/StreamingQueryExecutor.cs:86-97` | **third** path — per-batch `ResolveReferencedDocumentsAsync` + `ToPersistentObject` | Route each batch through the resolver. (Was missed in the first scoping pass.) |
| `Abstractions/EntityTypeDefinition.cs:31,35` | `DisplayFormat` + `DisplayAttribute` | **Replace** with a single `Breadcrumb` template field. |
| `Services/ModelSynchronizer.cs` | synthesizes `DisplayFormat`/`DisplayAttribute` | Synthesize/validate `Breadcrumb` instead. |

**Frontend — collapse duplicate display logic; server breadcrumb is authoritative:**

| Location | Current code | Action |
|---|---|---|
| `pipes/src/attribute-value.pipe.ts:43-68` | a **second template engine**: `resolveDisplayFormat` regex, `formatAsDetailValue`, and a hardcoded `['Name','Title','Street','name','title']` guess list; reads `entityType.displayFormat`/`displayAttribute` | **Delete the client-side display-name logic.** AsDetail nested objects get a server-emitted `breadcrumb` too; the pipe just returns `attr.breadcrumb ?? value`. |
| `pipes/src/reference-attr-value.pipe.ts` | `attr.breadcrumb ?? attr.value` | Fold into `attributeValue`; **delete** the standalone pipe (or keep one canonical reader — not two). |
| `pipes/src/reference-display-value.pipe.ts` | `selected.breadcrumb \|\| selected.name \|\| selectedId` (po-form) | Keep as the single picker-label reader, but it reads only `breadcrumb` (the server now always provides it); remove `name`/id guessing once breadcrumbs are guaranteed. |
| `pipes/src/as-detail-cell-value.pipe.ts` | `match.breadcrumb \|\| match.name` | Reduce to `breadcrumb` reader. |
| `pipes/src/reference-chips.pipe.ts` | reads `breadcrumbs[id]` map | **Keep** — already a pure presentation reader. |
| `models/src/entity-type.ts` | `displayFormat` / `displayAttribute` fields | **Replace** with `breadcrumb`. |

**Acceptance for this mandate:** after the work, a repo-wide grep for `displayFormat`/`displayAttribute`/`GetEntityDisplayName`/`ResolveDisplayFormat`/`ResolveReferencedDocumentsAsync` returns **zero** hits outside `BreadcrumbResolver` and its tests, and no frontend pipe contains a `{…}` template engine or a hardcoded display-property list.

---

## 3. Goals / non-goals

**Goals**
- A single declarative `[Breadcrumb("...")]` template per entity, authored against **collection property names**.
- Recursive resolution across references to arbitrary (bounded) depth, identical in all five surfaces.
- Request count for breadcrumbs that is **`O(depth)` per page**, independent of row count and reference fan-out — comfortably under 30.
- Works identically for collection-backed (PO detail) and projection-backed (query list) reads, regardless of whether a breadcrumb field is on the collection or only in the index.
- Fail-fast validation at model-sync time; no silent raw-id rendering.
- Preserve the existing row-level `Read` auth guarantee (R2-H10) through every recursion level.

**Non-goals**
- Client-side breadcrumb computation (stays server-side).
- General-purpose pagination perf rework of `QueryExecutor` (tracked separately; we only ensure breadcrumb resolution runs **after** Skip/Take).
- Localization of breadcrumb templates (templates are language-neutral; `TranslatedString` values inside are out of scope for v1).

---

## 4. Recommended solution

> **One server-side `BreadcrumbResolver` service, driven by a precomputed static dependency closure, resolving each page with breadth-first level-batched loading and pure in-memory rendering. The frontend stays a dumb string consumer.**

This is the most professional and resilient option because it (a) centralizes the logic at one chokepoint so all five surfaces are automatically consistent, (b) makes the request cost a function of breadcrumb *depth* not data *volume*, and (c) decouples breadcrumb computation from the projection shape, neutralizing the collection-vs-index trap.

### 4.1 Authoring model

- New class-level attribute **`[Breadcrumb("{Attr} literal {Attr2}")]`** in `MintPlayer.Spark.Abstractions`.
- **Replace** `EntityTypeDefinition.DisplayFormat` **and** `DisplayAttribute` with a single **`EntityTypeDefinition.Breadcrumb`** template field. (No backward compat: a bare `{Name}` template subsumes the old `DisplayAttribute` behavior.)
- `ModelSynchronizer` reads `[Breadcrumb]`; if absent, auto-synthesizes a default template (`{Name}` / `{FullName}` / `{Title}`, else the first scalar attribute, else the CLR type name) so every type always has a usable breadcrumb.

### 4.2 Template grammar (intentionally minimal)

```
template   := (literal | token)*
token      := '{' attributeName '}'
```
- Everything outside `{…}` is literal text (including `(`, `)`, spaces).
- `{{` / `}}` escape literal braces.
- A token resolves the **named attribute of the current entity**:
  - **scalar** → type-aware formatted value (dates/enums/numbers via the same formatting the wire uses), `null` → empty,
  - **single reference** → the **referenced entity's breadcrumb** (recursion),
  - **reference array** → each referenced entity's breadcrumb joined by a separator (default `", "`),
  - **AsDetail / complex** → its own breadcrumb if defined, else empty.
- No dot-paths (`{ParkedCar.LicensePlate}`) in v1: recursion is expressed by each type owning its own template, which is simpler, composable, and avoids leaking one type's field names into another's template. (Dot-paths can be added later without breaking this grammar.)

### 4.3 Static breadcrumb closure (startup, cached)

For each entity type, parse the template once into `Literal|Field` tokens and compute the **reference closure**: the transitive set of `(type → reference attribute → target type)` edges reachable through breadcrumb `Field` tokens that point at references. This is pure metadata (no DB). It yields, per root type, the maximum breadcrumb depth (longest *simple* path — cycle edges are cut) and the set of reference attributes to follow at each level.

**Cycle policy (refined during Phase 2):** cycles are **detected and surfaced** (diagnostic / startup log), but they are **not a hard error**. A self-referential breadcrumb is legitimate (e.g. an org chart `Person → Manager (Person)`), and the runtime is made safe by the `MaxBreadcrumbDepth` bound plus a per-path visited set — not by forbidding the shape. Erroring on cycles would be a foot-gun; bounding them is the resilient choice.

### 4.4 Runtime resolution — breadth-first, level-batched

`BreadcrumbResolver.ResolveAsync(session, rootEntities, rootType)`:

1. **Seed.** `loaded` map = root entities keyed by id. `frontier` = roots.
2. **BFS by level** (until frontier empty or `maxDepth`):
   - From the frontier types' breadcrumb closures, collect every referenced id that is **not already in `loaded`**.
   - **One batched call:** `session.LoadAsync<object>(ids[])` loads *all* of them — across mixed target types — in a **single RavenDB request** (type is recovered from document metadata). Ids already primed by the list query's `.Include()` resolve from the session cache for free.
   - Apply row-level `Read` auth in-memory on each newly loaded doc (no extra DB request); denied docs are recorded as **redacted**.
   - New docs become the next frontier.
3. **Render (pure CPU, zero DB).** For each entity needing a breadcrumb, walk its token list:
   - scalar → format; single ref → recurse into `loaded[id]`'s breadcrumb (memoized); array ref → join; redacted/missing → configurable placeholder (default `"—"`).
   - A per-path visited set + `maxDepth` guard breaks reference cycles (`A→B→A`).
   - Memoize breadcrumb-per-id so a doc referenced many times is rendered once.

**Request budget:** total breadcrumb DB requests per page = **number of BFS levels actually traversed (≤ `maxDepth`)** — independent of row count and fan-out. With `.Include()` priming level 1, the common case is **0–1 extra requests**.

### 4.5 The collection-vs-index trap, resolved

- **Referenced entities (level ≥ 1)** are always loaded as **collection documents** by id (references store collection ids) → every collection field the template names is present → always resolvable.
- **Root entities in a query/list (level 0)** arrive as **projections** (`VPerson`) that may lack the template's collection fields:
  - At model-sync time we precompute a per-type flag **`BreadcrumbProjectionSatisfiable`** = "every scalar token of the breadcrumb exists as a readable property on the projection type."
  - If satisfiable → render from the projection, **no extra load**.
  - If not → add the root ids into the level-0 batch and load their **collection documents** (one batched request for the whole page), render from those.
- The breadcrumb therefore **never depends on an index-computed field**; templates are authored against collection property names and resolve the same on the PO-detail page (collection) and the query list (projection ± one batch load). This is exactly the property the developer wanted that an index-side breadcrumb could not give.

### 4.6 Model-sync validation (fail fast)

When synthesizing each `EntityTypeDefinition`, validate the breadcrumb template and **throw a clear sync error** if:
- a token names a property/attribute that does not exist on the collection type,
- a reference token's target type has no resolvable `EntityTypeDefinition`,
- braces are unbalanced.

Cycles are **not** a validation failure — they are detected by the closure and surfaced as a diagnostic; the runtime depth + visited-set guard bounds them (see §4.3).

### 4.7 Integration — one chokepoint

`EntityMapper` stops computing display names ad hoc. `DatabaseAccess` (Get/List), `QueryExecutor` (custom + database queries), and the sub-query path all funnel through `BreadcrumbResolver` **after pagination**. The resolver returns breadcrumb-per-id; `EntityMapper` reads them out to populate `po.Breadcrumb`, `attr.Breadcrumb`, and `attr.Breadcrumbs[id]`. Because every surface shares this one resolver, consistency is structural, not by convention.

### 4.8 Frontend

No behavioral change beyond two small cleanups:
- Give `SparkSubQueryComponent` the same explicit single/array reference branches as query-list/po-detail so reference rendering is identical in the sub-query viewer.
- Multi-select picker labels already derive from `po.breadcrumb` → correct automatically once results carry recursive breadcrumbs.

### 4.9 Configuration

- `SparkOptions.MaxBreadcrumbDepth` (default **5**).
- `SparkOptions.BreadcrumbReferenceSeparator` (default `", "`).
- `SparkOptions.BreadcrumbRedactedPlaceholder` (default `"—"`).

---

## 5. Alternatives considered (and why rejected)

| # | Alternative | Verdict |
|---|---|---|
| A | **Compute breadcrumb inside the RavenDB index** | Rejected by the requester: value exists only in the index, absent on the collection-backed PO page. Also can't recurse arbitrarily without N nested `LoadDocument` joins. |
| B | **Depth-first per-reference recursion with `session.LoadAsync<T>(id)`** | Simplest to write; `O(rows × fan-out × depth)` requests → trips the 30-request limit immediately. Rejected. |
| C | **Resolve breadcrumbs on the frontend by walking `breadcrumbs` maps** | Pushes N round-trips to the client, duplicates logic across five components, can't see collection-only fields. Rejected. |
| D | **Multi-level `.Include()` path chains in the query** | RavenDB include-path chaining across heterogeneous types is brittle and query-shape-specific; doesn't cover the Get endpoint or custom queries uniformly. Kept only as the existing **level-1 optimization** feeding the BFS cache. |
| E | **Denormalize/cache the rendered breadcrumb on the document** | Stale on any upstream edit (a Person rename must rewrite every Car referencing them); needs subscription-worker fan-out. Over-engineered for the read-time need. Rejected for v1. |
| **F** | **Recommended: central resolver + static closure + BFS level-batched load** | `O(depth)` requests, one chokepoint, projection-agnostic, fail-fast. **Chosen.** |

---

## 6. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Reference cycles (`A→B→A`) | Static closure cycle detection (startup error) + runtime per-path visited set + `MaxBreadcrumbDepth`. |
| Wide fan-out still loads many docs in one request (payload size) | One request, but large id batch → acceptable; breadcrumb runs only on the **paginated page**, not the full set. Document the guidance. |
| Row-level auth leak through a referenced breadcrumb | Existing R2-H10 `Read` gate applied at every level; denied docs render the redacted placeholder. |
| `QueryExecutor` materializes-all-then-paginates today | Ensure breadcrumb resolution hooks **after** Skip/Take so only visible rows resolve; broader pagination perf is out of scope but noted. |
| Mixed-type batched `LoadAsync<object>` returning wrong CLR types | Resolver renders via `EntityTypeDefinition` looked up by the loaded doc's actual runtime type; tolerate nulls/denied. |

---

## 7. Success criteria

- A 3-level breadcrumb (`ParkingSpot → Car → Person`) renders correctly and identically on query-list, po-detail (single + array), sub-query viewer, and the multi-select picker.
- Listing N parking spots resolves all breadcrumbs in **≤ `MaxBreadcrumbDepth` + 1** RavenDB requests total (verified via `MaxNumberOfRequestsPerSession` assertions in tests).
- A breadcrumb referencing a collection-only field (`{FirstName}`) resolves correctly even when the list is served from a projection lacking that field.
- An invalid template (unknown attribute, unterminated cycle) fails at model-sync with a clear message.
- Denied row-level `Read` redacts the segment rather than leaking the value.

---

## 8. Test strategy (RavenDB-backed, per `reference_cronoscore_raven_tests.md`)

- Unit: template parser (tokens, escaped braces, errors); static closure (depth, cycle detection); renderer (scalar/single/array/redacted/cycle).
- Integration (embedded Raven): 3-level chain request-count assertion; projection-only-field root; reference-array breadcrumbs; auth-redaction; Get vs List vs custom-query parity.
- Frontend: snapshot the four read surfaces render the resolved strings; sub-query viewer parity after alignment.
```
