# Implementation plan — Issue #185 (AsDetail reference breadcrumbs)

See [issue_185_PRD.md](./issue_185_PRD.md). TDD: failing test first, then fix.

## Design

The fix is **server-first** and minimal: the only missing behaviour is that the breadth-first reference loader never collects ids that live inside embedded AsDetail children of the page roots. Everything downstream (the per-level batched load, the recursive renderer, and `EntityMapper` copying `breadcrumbs.Get(refId)` onto each reference attribute) already works — it just never sees those ids.

### Key facts (verified)
- `EntityMapper.PopulateAttributeValues` already sets `attribute.Breadcrumb = breadcrumbs.Get(refId)` for single refs (EntityMapper.cs:226-229) and a per-id map for ref arrays (231-243), and is invoked recursively for AsDetail children (PopulateAsDetail → PopulateAttributeValues, line 283). **No change needed there for AC1** once the resolver loads the ids.
- The resolver loads referenced docs by id (`LoadAsync<object>(ids)`), so it is page-independent. The "first page" symptom is frontend-only.
- AsDetail children are materialized as POs **only for the roots** (depth 1) — deep breadcrumb targets render as a string. So AsDetail descent is needed **only at depth 1**, recursively through nested AsDetail.
- `EntityAttributeDefinition`: `DataType == "AsDetail"`, `AsDetailType` = child CLR full name, `IsArray`. Child def via `modelLoader.GetEntityTypeByClrType(AsDetailType)`.

## Steps

### Step 1 — Failing test (resolver descent) — `tests/.../Services/BreadcrumbResolverTests.cs`
Add entities mirroring the repro shape (page-independent — paging is irrelevant to the resolver):
- `BR_Artist { Id, string Name }` with breadcrumb `{Name}`.
- `BR_SongArtist { string ArtistId }` (embedded, `[Reference]`-style — modelled via `Ref("ArtistId", typeof(BR_Artist))`), breadcrumb `{ArtistId}`.
- `BR_Song { Id, string Title, List<BR_SongArtist> Artists }`, breadcrumb `{Title}`, with an `AsDetail` array attribute `Artists` (`DataType="AsDetail"`, `AsDetailType = typeof(BR_SongArtist).FullName`, `IsArray=true`).

Seed e.g. 50 artists + a song crediting `artists/40`, `artists/41`, `artists/42`. Call `resolver.ResolveAsync(session, [song], songDef)` and assert:
- `result.Get("artists/40") == "Logic"`, `…/41`, `…/42` all resolved (currently absent ⇒ **fails**).
- Request count is O(depth) for the AsDetail fan-out (one batched load for the artists level) — independent of artist-collection size.

Add helper for AsDetail attribute definition (mirrors `EntityMapperAsDetailTests`):
```csharp
private static EntityAttributeDefinition AsDetailArr(string name, Type child) =>
    new() { Id = Guid.NewGuid(), Name = name, DataType = "AsDetail", AsDetailType = child.FullName, IsArray = true };
```

Run: `dotnet test tests/MintPlayer.Spark.Tests --filter BreadcrumbResolverTests` → confirm red.

### Step 2 — Implement resolver descent — `Services/Breadcrumb/BreadcrumbResolver.cs`
At depth 1, when collecting `needed` ids, also walk AsDetail children. Replace the inline `GetAllReferences(def)` id-collection with a routine that yields direct reference ids **and** recurses through AsDetail children:

```csharp
// depth == 1: collect from root refs AND embedded AsDetail children (recursive).
private void CollectRootReferenceIds(object entity, EntityTypeDefinition def, ICollection<string> into)
{
    foreach (var reference in GetAllReferences(def))
        foreach (var refId in ExtractIds(entity, reference.AttributeName))
            into.Add(refId);

    foreach (var attr in def.Attributes.Where(a => a.DataType == "AsDetail" && !string.IsNullOrEmpty(a.AsDetailType)))
    {
        var childDef = modelLoader.GetEntityTypeByClrType(attr.AsDetailType!);
        if (childDef is null) continue;
        foreach (var child in ReadChildren(entity, attr.Name)) // single or each array element
            CollectRootReferenceIds(child, childDef, into);
    }
}
```
- `ReadChildren` reads `ReadValue(entity, name)` and yields the single embedded object or each element of the embedded collection (skip nulls/strings — reuse the `IEnumerable` shape from `ExtractIds`).
- Keep the existing `neededSet`/`denied`/`renderEntity.ContainsKey` dedup when funnelling collected ids into `needed`.
- Deeper levels (`depth > 1`) keep using `closure.GetReferences(def)` unchanged.
- The collected referenced ids enter the existing `needed → LoadAsync → security gate → renderEntity/defById → next` flow untouched, so rendering, cycle-safety, MaxDepth, and redaction all apply automatically (AC2, AC6).

Run Step 1 test → green.

### Step 3 — Embedded row's own breadcrumb (#184 / AC3) — `Services/EntityMapper.cs`
An embedded AsDetail row has no id, so `breadcrumbs.Get(po.Id)` falls back to the type name (EntityMapper.cs:190-194). Render the embedded type's `[Breadcrumb]` template instead, reusing the resolved reference breadcrumbs.

Options (pick the smallest faithful one during implementation):
- **(a)** Have `BreadcrumbResolver` expose a pure `Render`-from-entity helper (template + a `BreadcrumbResult` for reference tokens) and call it from `PopulateAsDetail` for each child, setting `child.Breadcrumb`/`child.Name`.
- **(b)** In `PopulateAttributeValues`, when `po.Id` is null but the def has a `Breadcrumb` template, render it inline by substituting scalar values and `breadcrumbs.Get(refId)` for reference tokens.

Prefer (a) — single source of truth for template rendering. Add a focused test (AC3) asserting `objects[i].Breadcrumb` equals the referenced name (e.g. `"Logic"`).

### Step 4 — Nested AsDetail recursion test (AC4)
Extend with `Person → Jobs[] → Certifications[].IssuerId → Issuer`; assert the deepest reference resolves. (Step 2's recursion already covers it; this pins it.)

### Step 5 — Get-endpoint integration test (AC1/AC3 end-to-end) — `tests/.../Endpoints/PersistentObject/` or `EntityMapperAsDetailTests.cs`
Through `SparkEndpointFactory` + `SparkClient.GetPersistentObjectAsync` (or `EntityMapper.ToPersistentObject` with a real resolver), seed the repro and assert the returned PO's `objects[i].attributes[ref].Breadcrumb` are the artist names.

### Step 6 — Frontend: prefer server breadcrumb
- `models/src/as-detail-conversions.ts`: stop discarding the per-attribute `breadcrumb` on the display path. Add a display-row builder (e.g. carry a side map of `{ [attrName]: breadcrumb }` on the row, or a `nestedPoToDisplayRow`) used by `arrayValue` — **without** polluting the form-save dict consumed by `dictToNestedPo`.
- `pipes/src/array-value.pipe.ts`: use that display-row builder so the row reaching the cell pipe carries breadcrumbs.
- `pipes/src/as-detail-cell-value.pipe.ts`: prefer the server breadcrumb for a reference cell; fall back to the options page, then `String(value)`.
- Vitest unit test for `AsDetailCellValuePipe` (AC5): server breadcrumb present → used even when the id is absent from options.
- **Vitest unit test for `as-detail-conversions.ts`** (per user request): cover `nestedPoToDict` / `dictToNestedPo` round-trips for primitive, single-AsDetail, and array-AsDetail shapes, and pin the new behaviour that the display path preserves the per-attribute `breadcrumb` while the form-save path (`dictToNestedPo`) still ignores it. Guards this central conversion against regressions.

> Per global Angular/Spark guidance: do **not** run `ng serve` / `ng build` / `ng test` against an embedded-dev-server workspace. Run the FE unit test via the workspace's Vitest target (`nx test ng-spark` or the project's configured `vitest` runner) only — confirm the existing test command in the project before invoking.

### Step 7 — Verify & wrap up
- `dotnet test tests/MintPlayer.Spark.Tests` (full) green.
- Bump preview versions per the repo's release convention (NuGet preview.* and `@mintplayer/ng-spark`) if the change is to be published — confirm with the existing version-bump pattern before editing.
- Update [breadcrumbs-plan.md](./breadcrumbs-plan.md) / memory to record that #184's AsDetail descent is now actually implemented (it was only closed-as-completed before).

## Risk / cost notes
- Descent is bounded to depth-1 roots' AsDetail subtree; collected ids flow into the existing single per-level batched `LoadAsync`, so cost remains O(depth) (AC2) — strictly cheaper than today's frontend path (one `executeQueryByName` per AsDetail reference column per detail view).
- Cycle-safety / MaxDepth / redaction are inherited because collected ids use the same frontier machinery.
