# Issue #384 — the embedded `[Breadcrumb]` renderer is dead for every keyed value object

**Status:** Planned — see [issue_384_plan.md](issue_384_plan.md)
**Issue:** [#384](https://github.com/MintPlayer/MintPlayer.Spark/issues/384)

## The problem

`EntityMapper.PopulateAttributeValues` decides whether to render an embedded object's own
`[Breadcrumb]` template by testing whether the object has an id.

`libs/spark/MintPlayer.Spark/Services/EntityMapper.cs:213-226`

```csharp
// Embedded AsDetail objects have no id and aren't keyed in the result → render their own
// [Breadcrumb] template in place, ...
var breadcrumb = breadcrumbs?.Get(po.Id);
if (string.IsNullOrWhiteSpace(breadcrumb) && breadcrumbs is not null && string.IsNullOrEmpty(po.Id))
{
    var def = modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name);
    breadcrumb = EmbeddedBreadcrumbRenderer.Render(...);
}
if (string.IsNullOrWhiteSpace(breadcrumb))
    breadcrumb = entityType.Name;
```

The comment states the premise outright, and the premise is no longer true. Eight lines above,
`4f9e9319` (#382) changed where `po.Id` comes from:

```csharp
var keyProperty = Abstractions.Model.SparkValueObjects.GetKeyPropertyName(entityType) ?? "Id";
var idProperty = entityType.GetCachedProperty(keyProperty);
po.Id = idProperty is not null ? AccessorCache.GetGetter(idProperty)(entity)?.ToString() : null;
```

An embedded row's `po.Id` is now its registered `[ValueKey]` row key. `BreadcrumbResult` is keyed by
**document id** — `BreadcrumbResolver` reads a property literally named `Id`
(`BreadcrumbResolver.cs:445`) and only for non-empty values (`:69`). A row key is therefore never a
map hit. So `breadcrumbs.Get(po.Id)` returns null, `string.IsNullOrEmpty(po.Id)` is **false**, the
renderer is skipped, and the row falls through to the CLR type name.

`ValueObjectKeyGenerator` mints `public string Id { get; set; } = Guid.NewGuid().ToString("N")` for
every `[ValueObject]` without an explicit `[ValueKey]`
(`ValueObjectKeyGenerator.Producer.cs:68-69`), and the initializer runs during deserialization — so
even a legacy row stored without a key comes back carrying one. **In memory every embedded
collection row is keyed.** The embedded-breadcrumb path is not degraded; it is dead.

## Three corrections to the issue

The issue is right about the mechanism and the fix. Its framing is wrong in three ways that matter
for how this gets verified, so they are recorded here rather than discovered during review.

### 1. The bug predates #382

The gate arrived in `80d4af4d` (#186, 2026-06-09) already carrying `&& string.IsNullOrEmpty(po.Id)`.
Before #382 the key came from a property literally named `Id`, so the renderer was already skipped
for any embedded type that happened to own one — which is exactly DemoApp's `Address`
(`apps/DemoApp/DemoApp.Library/Entities/Address.cs:6`, `public string? Id { get; set; }`, and
`GetKeyPropertyName` falls back to `"Id"` when nothing is registered).

#382 did not introduce the failure. It widened it from *types that happen to own an `Id`* to *every
keyed value object*, which is now the default shape. The issue's "regression introduced by #382" is
half right, and the half it misses is the half that says a test would have caught this in June.

### 2. Neither template the issue names is user-visibly broken today

The issue says two shipped templates are affected: `ProjectColumn` and DemoApp's `Address`. Both are
broken at the mapper, and **neither reaches a screen that shows it.**

- **`ProjectColumn` is in an AsDetail array.** An array renders as a table whose columns come from
  the row type's *model attributes* (`as-detail-columns.pipe.ts:8-11`). The row's own breadcrumb is
  a reserved dict key, never a column. Verified live on coverage.mintplayer.com: the `Columns` grid
  renders `Todo / In Progress / To Review / Done` from the `Name` attribute, and no type name
  appears anywhere. Same for `Build.Sessions`, `GitHubProject.EventMappings`,
  `OidcApplication.Claims`/`Secrets`, HR `Person.Jobs`.
- **DemoApp's `Address` is bypassed by a custom renderer.** `Person.Address` is single, carries
  `"renderer": "address-card"`, and the detail renderer draws `Street`/`City`/`State` straight from
  `po.attributes`. On the edit form there is no edit renderer (deliberately, per #241/#245), so it
  falls to `asDetailDisplayValue` → `selfBreadcrumb`, which **filters the placeholder** and shows
  `"Click to edit"`. It is not in any query grid (`inQueryType: false`).

The issue's repro steps do not reproduce. That is the single most expensive thing in it: a plan that
trusted them would verify the fix against two screens that never showed the bug, and conclude from
green pixels that a dead code path had been revived.

### 3. So what *is* the user-visible cost?

Today: **none that we can demonstrate.** The single-AsDetail types that do surface a breadcrumb —
`Build.Feedback`, `Build.GateSnapshot`, `Build.Patch`, `Repository.Gate`, HR `Person.Address` — are
all **keyless** (no `[ValueObject]`, no `Id` property), so the gate still fires and they render
correctly. The set of types that are broken and the set that are displayed do not currently
intersect.

That makes this a latent defect, not an outage, and the PRD says so plainly rather than inheriting
the issue's urgency. It is still worth fixing, for a reason that survives the correction: **#382 made
keyed the default shape for every `[ValueObject]`.** The next embedded type that wants a breadcrumb
on a detail page or a query grid gets a CLR type name, and the author has no way to tell that the
feature was already dead before they used it. Fixing a dead mechanism is cheaper than fixing it
plus the report from whoever trips over it.

## An adjacent defect, found while tracing the symptom

The type-name placeholder is filtered on **two** of the four paths that display an embedded
breadcrumb. `selfBreadcrumb(row, typeName)` (`as-detail-conversions.ts:97-109`) exists precisely to
strip it, and is called from `as-detail-cell-value.pipe.ts:99-102` and
`as-detail-display-value.pipe.ts:21-22`. Two other pipes read the same server value **without** it:

- `pipes/src/attribute-value.pipe.ts:29` — `if (attr.object.breadcrumb) return attr.object.breadcrumb;`
  (single AsDetail, read-only detail page)
- `pipes/src/query-cell.pipe.ts:33` — `if (cell.breadcrumb) return cell.breadcrumb;` (query grid cell,
  fed by `QueryResultProjector.cs:144`)

This is a **separate, live, user-visible bug** with a **keyless** type: when the template renders
blank, `EntityMapper.cs:225-226` substitutes the type name and these two pipes print it. A `Build`
whose `Feedback.State` is empty renders the literal `BuildFeedback`; `Repository.Gate` renders
`GateSettings`; HR `Person.Address` renders `Address` on both its detail page and the Person grid.

It is in scope. Per the repository's one-PR rule this lands with #384 rather than becoming a
follow-up, and the two defects are genuinely one subject: both are the placeholder at
`EntityMapper.cs:225-226` escaping to a user. Keeping them apart would mean shipping a fix for the
dead path while leaving the live one printing `GateSettings` at somebody.

## Requirements

- **FR1** — an embedded object whose type has a **populated row key** renders its own `[Breadcrumb]`
  template. This is the issue's fix: drop the `&& string.IsNullOrEmpty(po.Id)` term.
- **FR2** — an embedded object with **no** key keeps rendering its template, exactly as today. The
  existing behaviour is not disturbed.
- **FR3** — a **root** persistent object keeps taking its breadcrumb from the pre-resolved
  `BreadcrumbResult`, including the redaction placeholder for a denied reference. No redaction
  bypass, and no per-row re-render of something the resolver already resolved.
- **FR4** — the CLR type-name placeholder never reaches the user through `attribute-value.pipe` or
  `query-cell.pipe`; those fall through to the same emptiness the two `selfBreadcrumb` callers
  already produce.
- **FR5** — the regression is **pinned by a test over a keyed type**. A keyless fixture passes with
  or without the fix, which is the whole reason this survived three months and two PRs.

## Design

### The gate

```diff
-if (string.IsNullOrWhiteSpace(breadcrumb) && breadcrumbs is not null && string.IsNullOrEmpty(po.Id))
+if (string.IsNullOrWhiteSpace(breadcrumb) && breadcrumbs is not null)
```

The remaining two conditions already state the real intent: *the pre-resolved result had no
breadcrumb for this object, so render its template in place.* An embedded row is absent from
`breadcrumbs` because it is embedded, not because it is unkeyed. This is what `10f2a1cc` did on the
abandoned branch; the comment above it is rewritten to describe the lookup, not the id.

**Why this is safe.** Every call site was enumerated. `PopulateAttributeValues` is reached from
`DefaultPersistentObjectActions.cs:161` and `RowSecurityGate.cs:199` (which every query path funnels
through), and both resolve breadcrumbs over exactly the entities they then map — so no root object
arrives with a populated id and no map entry. The residual cases all land on identical output:

| Case | Today | After |
|---|---|---|
| `breadcrumbs is null` | block skipped | block skipped — the second clause still guards it |
| Root whose template renders `""` | type name | renderer runs the same `def`, yields `""`, same type name |
| Root with no template | resolver returned `def.Name`, non-empty → not entered | unchanged |
| Root with a denied reference | resolver returned `RedactedPlaceholder`, non-empty → not entered | unchanged; and the renderer resolves reference tokens through the same `breadcrumbs.Get`, so **the placeholder is preserved even if entered** |
| Projection with no `EntityTypeDefinition` | `def` null → type name | renderer's marker fallback returns null → same type name |

### The two unfiltered pipes

Route both through the existing `selfBreadcrumb`, which already encodes the comparison (last dotted
segment, to cope with `EntityType.name` being short and `asDetailType` being the full CLR name).
This is a reuse, not a new rule — a second implementation of "is this the placeholder?" is exactly
how the first one came to cover half the paths.

Deliberately **not** attacking it at `EntityMapper.cs:225-226` by suppressing the placeholder
server-side. That is the tempting single-point fix and it is the wrong one here: `po.Name` is set
from the same string, the client's `selfBreadcrumb` contract is written around the placeholder
existing, and `QueryResultProjector.cs:144`'s comment documents a cell pipe that tests `Breadcrumb`
before `Value`. Changing what the server emits would ripple through all four paths plus `po.Name`,
to fix two pipes. Revisit it as its own subject if the placeholder ever earns a third consumer.

## Out of scope

- **Closing PR #381.** It is still `OPEN`, superseded de facto by #382, and carries unrelated work
  (`DeleteBranchOnPrClose`, the AsDetail server lifecycle). Its fate is a separate decision.
- **Suppressing the placeholder in `EntityMapper`.** Argued above.
- **Making `BreadcrumbResolver` key by row key.** It keys documents; embedded rows are the case that
  must be allowed to miss. Rendering in place is the design, not a workaround for it.

## Risks

- **R1 — a root object starts rendering in place where it used to show a type name.** Only reachable
  if the resolver and the mapper disagree about an id, which for a root they cannot: both read `Id`.
  If it happened, the output is strictly better than the type name.
- **R2 — the fix is unverifiable against a shipped screen** (correction 2). Handled by S1/S2 in the
  plan: prove the mapper-level behaviour with a keyed test, and prove the *display* behaviour against
  a shape that actually shows a breadcrumb — the adjacent defect supplies one.
- **R3 — cost of re-rendering.** `EmbeddedBreadcrumbRenderer` now runs for embedded rows that
  previously short-circuited. It is in-memory template substitution over an already-loaded entity
  with a depth guard, on rows the mapper is walking anyway. No new loads.
