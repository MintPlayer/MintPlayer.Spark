# PRD — Issue #185: AsDetail reference cells resolve labels by page membership, not by id

- **Issue:** [#185](https://github.com/MintPlayer/MintPlayer.Spark/issues/185)
- **Closes (also):** [#184](https://github.com/MintPlayer/MintPlayer.Spark/issues/184) — the latent server-side bug #185 is the user-visible manifestation of. #184 was closed when #183 shipped, but the AsDetail descent it described was never implemented.
- **Family:** #182 (renderers in AsDetail), #183 (recursive breadcrumbs), #184 (embedded breadcrumb resolution). "AsDetail is the overlooked read-surface."
- **Status:** Draft

## Problem

In a PO **detail** view, an **AsDetail** sub-table's **`Reference` cell** shows the referenced document's name only when that document happens to be on the **first page** of the reference query's results. Otherwise it renders the **raw id** (e.g. `Artists/40`).

Concrete repro (`MintPlayer.Web`, ~138 `Artists`): `GET /spark/po/song/Songs/43` ("1-800-273-8255") credits `Artists/40` (Logic), `Artists/41` (Alessia Cara), `Artists/42` (Khalid). The detail page's Artists table renders:

| Row | ArtistId | Rendered cell |
|----|----------|---------------|
| 1 | `Artists/40` | `Artists/40` ❌ raw id |
| 2 | `Artists/41` | **Alessia Cara** ✅ |
| 3 | `Artists/42` | `Artists/42` ❌ raw id |

Only `Artists/41` resolves — "Alessia Cara" sorts onto `GetArtists` page 1; Logic/Khalid sort past the page cut-off. This breaks reference labels in AsDetail grids as soon as the referenced collection exceeds one options page. It also regressed previously-working data (with one artist seeded everything resolved; after seeding 138, almost none do).

## Root cause (two layers)

### Server (`MintPlayer.Spark`) — the real defect
`Services/Breadcrumb/BreadcrumbResolver.cs` resolves references breadth-first for the page **roots** via `GetAllReferences(def)` (root `[Reference]` attributes only — it reads `def.Attributes` and stops). It does **not** descend into references nested inside embedded **AsDetail** children. So `objects[i].attributes[ref].breadcrumb` comes back `null`:

```json
{ "name": "Artists", "objects": [
  { "attributes": [ { "name": "ArtistId", "value": "Artists/40", "breadcrumb": null }, … ] },
  …
]}
```

`EntityMapper.PopulateAttributeValues` (EntityMapper.cs:226-229) **already** copies `breadcrumbs.Get(refId)` onto each child reference attribute — it is invoked recursively for AsDetail children (PopulateAsDetail → PopulateAttributeValues, EntityMapper.cs:283). The breadcrumb is `null` solely because the resolver never loaded/rendered those referenced ids.

The resolver loads referenced documents **by id** (`session.LoadAsync<object>(ids)`), so server resolution is inherently **page-independent** — the "first page" failure is purely a frontend artifact.

### Frontend (`@mintplayer/ng-spark`) — resolves by page, drops the server breadcrumb
- `po-detail/src/spark-po-detail.component.ts` → `loadAsDetailTypes()` runs each AsDetail `Reference` column's query **once with no paging override** and stores `result.data` (one default page) as the options list.
- `pipes/src/as-detail-cell-value.pipe.ts` resolves a cell against that single page (`options.find(o => o.id === value)`), else falls back to the raw id.
- `models/src/as-detail-conversions.ts` → `nestedPoToDict()` flattens each attribute to its `value`, **dropping** the per-attribute `breadcrumb` — so even once the server emits a breadcrumb on the embedded reference attribute, the cell pipe never receives it.

## Goals

1. **Server emits the per-row breadcrumb** on embedded AsDetail reference attributes (`objects[i].attributes[ref].breadcrumb`), resolved through the existing batched `BreadcrumbResolver` extended to descend (recursively) into AsDetail children — same depth-bound, cycle-safety, and level-batched `LoadAsync`.
2. **Frontend prefers the server breadcrumb** on an AsDetail reference cell, falling back to the options page (then the raw id) only when absent.
3. **Embedded AsDetail row's own breadcrumb** resolves its `[Breadcrumb]` template (closes #184's first bullet), recursing into referenced entities — rather than rendering the CLR type name.
4. **Cost stays O(breadcrumb depth)** per page — no N+1, no dependence on a referenced collection fitting in one page.

## Non-goals

- Changing the wire format / attribute shape (the `breadcrumb` / `breadcrumbs` fields already exist on the attribute model, FE and BE).
- Changing reference-query paging semantics, sort, or the options editor.
- Resolving AsDetail references for entities that appear only as *deep* breadcrumb targets (depth > 1). Those are represented solely by their breadcrumb string and their AsDetail children are never materialized as POs, so they need no descent.

## Acceptance criteria

- **AC1 (server, #185 core):** For a parent with an AsDetail array whose children reference documents that do **not** all fit on the reference query's first options page, the PO returned by `GET /spark/po/{type}/{id}` has a non-null, correct `breadcrumb` on **every** embedded reference attribute (`objects[i].attributes[ref].breadcrumb`). On the repro shape: Logic, Alessia Cara, Khalid all resolve.
- **AC2 (server, O-depth):** Resolving a multi-row AsDetail parent costs **O(breadcrumb depth)** RavenDB requests (one batched `LoadAsync` per level), independent of the number of AsDetail rows and independent of the referenced collection size — asserted via `session.Advanced.NumberOfRequests`.
- **AC3 (server, #184):** Each embedded AsDetail row's own `breadcrumb` renders its `[Breadcrumb]` template (recursing into referenced entities), not the CLR type name.
- **AC4 (server, recursion):** Nested AsDetail-within-AsDetail reference attributes (e.g. `Person → Jobs[] → Certifications[].IssuerId`) also resolve.
- **AC5 (frontend):** An AsDetail reference cell renders the server `breadcrumb` when present, regardless of whether the referenced doc is on the options first page; falls back to options then raw id only when the server breadcrumb is absent.
- **AC6 (safety):** Cycles and the `MaxDepth` bound behave identically to the top-level path; row-level read redaction still applies to AsDetail-nested references.

## Test plan (TDD — write failing first)

Primary tests use **`SparkTestDriver`** (embedded RavenDB) — server-side, page-independent:

1. **`BreadcrumbResolverTests`** (new cases): seed a parent with an AsDetail array of children, each referencing a doc; assert the resolver's `BreadcrumbResult` contains the referenced docs' breadcrumbs (proves descent + load). Assert request count is O(depth) for N rows. Add a nested-AsDetail case (AC4) and a cycle/MaxDepth case (AC6).
2. **Get-endpoint / EntityMapper integration** (new case via `SparkEndpointFactory` + `SparkClient` or `EntityMapperAsDetailTests`): seed parent + many referenced docs; load the PO; assert `objects[i].attributes[ref].Breadcrumb` is the referenced name for rows beyond any first page, and the embedded row's own `Breadcrumb` resolves its template (AC1, AC3).

Secondary (frontend): a Vitest unit test for `AsDetailCellValuePipe` asserting it prefers the server breadcrumb (AC5).

**Workflow:** add the resolver test → run → watch it fail (referenced ids absent / breadcrumb null) → implement the resolver descent → green → layer in the remaining cases and the frontend change.
