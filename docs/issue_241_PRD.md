# PRD: Renderer value for AsDetail attributes (#241) + row context for renderers (#245)

**Status: implemented.** Resolves #241 and #245 in one PR, plus a latent third bug both issues orbit.
Client-only — **no server change**; `EntityMapper`'s `attr.Value = null` for AsDetail
(`libs/spark/MintPlayer.Spark/Services/EntityMapper.cs:276`) is deliberate and stays.
Ground truth verified against `master` `32d03c3` (2026-08-15).

## Problem

### #241 — AsDetail renderers receive `undefined` value

A custom attribute renderer (`provideSparkAttributeRenderers`) attached to an **AsDetail**
attribute receives `value: undefined` in every PO-reading host. The server deliberately nulls
the flat `Value` for AsDetail attributes (the nested PO(s) carry the data in `object`/`objects`),
but the client renderer-input builders pass only `itemAttr?.value` — so the renderer gets
nothing, for exactly the attributes that need custom rendering most (embedded value objects
like a `CoverageSummary`).

### #245 — renderers get no row context

No builder passes the row, so a renderer cannot render a cell that combines two fields of one
row (repository name + private badge, `runId.attempt`, sha + fullName).

### Latent — every contract member is required, and `NgComponentOutlet` throws on undeclared inputs

Every builder passes its full input bag unconditionally to `NgComponentOutlet`, which **throws
when the component doesn't declare an input**. A renderer omitting `options` (say) breaks today.
Any new optional input (like `item`) would force-break every existing renderer unless the bag is
filtered to the component's declared inputs.

## Motivating consumer

Coverage (MintPlayer/CodeCoverage) wants a `coverage-bar` renderer on `Repository.LatestCoverage`,
`Commit.Coverage`, `Build.Coverage` — all AsDetail `CoverageSummary` attributes — so the generic
query list / sub-query / detail hosts render the same progress bar its hand-written pages show.
With `item`, a `repo-name` renderer adds the inline *private* badge and the `short-sha` renderer
becomes a commit link, completing master-parity of the generic grids.

## Ground truth: the 7 renderer-input builders (4 components)

| # | Site | value source | AsDetail state today | #241 fix | gets `item` (#245) |
|---|---|---|---|---|---|
| 1 | `query-list` cell (`spark-query-list.component.ts` `getColumnRendererInputs`) | row `PersistentObject` attr | `value: null` | ✅ `object ?? objects` | row PO |
| 2 | `sub-query` cell (`spark-sub-query.component.ts` `getColumnRendererInputs`) — duplicate of 1 | row PO attr | `value: null` | ✅ | row PO |
| 3 | `po-detail` field (`spark-po-detail.component.ts` `getDetailRendererInputs`) | detail PO attr | `value: null` **and the `formData` loop maps every AsDetail key to `null` too** | ✅ both `value` and the `formData[a.name]` loop | detail PO |
| 4 | `po-detail` AsDetail sub-table cell (`getAsDetailCellRendererInputs`) | flat `Record` from `nestedPoToDisplayRow` | already populated (flattening recurses) | — | flat row record |
| 5 | `po-form` edit (`getEditRendererInputs`) | `formData()[attr.name]` via `nestedPoToDict` | already populated (nested dict) | — | — (out of scope) |
| 6 | `po-form` AsDetail display cell (`getAsDetailCellRendererInputs`) | flat row record | already populated | — | flat row record |
| 7 | `po-form` AsDetail edit cell (`getAsDetailCellEditRendererInputs`, mutates row in place) | flat row record | already populated | — | flat row record |

Key shape facts:

- The client model is **structural** — there is no `PersistentObjectAttributeAsDetail` client
  type. `object?: PersistentObject | null` / `objects?: PersistentObject[] | null` live on the
  single `PersistentObjectAttribute` interface (`models/src/persistent-object-attribute.ts`).
  The fallback reads them directly.
- Query rows and detail POs are the identical shape (both come from
  `EntityMapper.ToPersistentObject`), so sites 1–3 share one fallback helper.
- Projection caveat: when a projection lacks the property, `EntityMapper` skips population and
  the renderer sees the **scaffolded** child (a structured PO with null values / `[]`), never
  `undefined`. Documented, not special-cased.
- The same registered `columnComponent` sees a `PersistentObject` at sites 1–2 and a flat
  `Record` at sites 4/6/7 (which may carry the reserved `'__sparkBreadcrumbs'` key). One `item`
  name, union-typed, documented.

## Design

**The genuinely shared part is the input *filter*, not the bag *assembly*.** A single
mega-builder would need a discriminated union over "PO + attr" / "flat row + col" /
"formData + attr" — a shallow pass-through wearing a union type. Instead:

### A. `renderers/src/renderer-inputs.ts` (new, exported from `@mintplayer/ng-spark/renderers`)

Two small functions, reusable by downstream apps for their own outlets:

```ts
const declaredInputs = new Map<Type<any>, Set<string>>();
/** Drops entries the component doesn't declare, so every contract member becomes genuinely optional. */
export function withDeclaredInputs(component: Type<any>, inputs: Record<string, any>): Record<string, any>;

/** The renderer-facing value of an attribute: flat value, or the AsDetail nested PO(s). */
export function rendererValue(attr: PersistentObjectAttribute | undefined): any; // value ?? object ?? objects
```

`reflectComponentType` is public Angular API since v14 (repo pins 22.x). The `Map` cache is
load-bearing: all 7 builders are template expressions re-evaluated every CD pass, and
query-list supports virtual scrolling.

### B. Apply per site

- **Sites 1–3**: `value: rendererValue(itemAttr)`. Site 3 additionally fixes its formData loop:
  `formData[a.name] = rendererValue(a)`.
- **All 7 sites**: add `item` to the bag where the table says so, change each builder signature
  to take the resolved component type first (every template already has it as the
  `@if (…; as X)` alias — no second registry lookup), and return
  `withDeclaredInputs(component, bag)`.

### C. Contracts (`renderers/src/spark-attribute-renderer.ts`) — all members optional, plus `item`

```ts
export interface SparkAttributeColumnRenderer {
  value?: InputSignal<any>;
  attribute?: InputSignal<EntityAttributeDefinition | undefined>;
  options?: InputSignal<Record<string, any> | undefined>;
  /** The row this cell belongs to: a PersistentObject in query-list/sub-query grids,
      a plain record (possibly incl. '__sparkBreadcrumbs') in AsDetail sub-tables.
      Passed only when declared. */
  item?: InputSignal<PersistentObject | Record<string, any> | undefined>;
}
export interface SparkAttributeDetailRenderer {
  value?; attribute?; options?;
  formData?: InputSignal<Record<string, any>>;
  /** The full PersistentObject — ids/breadcrumbs the flattened formData drops. */
  item?: InputSignal<PersistentObject | undefined>;
}
export interface SparkAttributeEditRenderer {
  value?; attribute?; options?;
  valueChange?: InputSignal<(value: any) => void>;
}
```

Value contract, documented on `value`: *for an AsDetail attribute this is the nested
`PersistentObject` (single) or `PersistentObject[]` (array) in PO-backed hosts, and the
flattened dict / array of dicts in form/AsDetail-cell hosts.* That asymmetry exists today and
is preserved deliberately (the form paths are dict-native end to end); renderers that serve
both hosts normalize both shapes (a name→value map from `attributes` when present).

**No contract break**: `value` stays the single `InputSignal<any>` input; existing renderers
declaring the full old contracts behave byte-identically. An AsDetail attribute never had a
usable value to regress.

### D. Registration slots become nullable

`SparkAttributeRendererRegistration.detailComponent`/`columnComponent` were required; the
po-form spec already faked `columnComponent: null`. Both become `Type<any> | null` optional so
single-slot registrations stop lying to the type system.

### E. Behavioral notes documented (not changed)

- Filtering `valueChange` away when an edit renderer doesn't declare it silently disables
  write-back instead of throwing. Correct — not declaring it means not wanting it — noted in
  the guide's "Key points".
- Projection-scaffold case (see shape facts) documented in the guide's AsDetail section.

## Acceptance criteria

1. A `columnComponent`/`detailComponent` registered on an AsDetail attribute receives the
   nested PO (single) / PO array (array) as `value` in query-list, sub-query, and po-detail
   field hosts; detail `formData` carries the same for AsDetail keys.
2. A renderer declaring `item` receives the row (PO in grids, record in AsDetail cells); one
   declaring only `value` renders without `NgComponentOutlet` errors in every host.
3. Existing renderers (declaring the full old contracts) behave byte-identically; the po-form
   B3 tests pass unmodified.
4. Projection-scaffold case: an AsDetail attribute whose projection lacks the property yields
   the scaffolded child PO, not undefined (documented).
5. `docs/guide-custom-attribute-renderers.md` updated: contract blocks, input matrix (add
   `item`, note passed-only-when-declared), new "AsDetail values" and "Row context" sections,
   root-import examples fixed to `@mintplayer/ng-spark/renderers` (+ `/models`).
6. Version bump: `@mintplayer/ng-spark` → **22.0.11** (client-only; .NET packages and
   ng-spark-auth untouched).

## Downstream validation (Coverage, ready to consume)

- `coverage-bar` renderer already normalizes the nested-PO shape — lights up on the version
  bump alone (#241).
- With `item`: `repo-name` adds the inline *private* badge (`Name` + `IsPrivate`), `short-sha`
  becomes a commit link (`LatestCoverageSha` + `FullName`).
