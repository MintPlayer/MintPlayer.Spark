# Plan — Issue #254: `[IgnoreProperty]`

**PRD:** [issue_254_PRD.md](issue_254_PRD.md) · **Branch:** `feat/issue-254-ignore-property-attribute`

Seven milestones, each independently committable. Per repo convention the full test suite runs once
at the end (M7), not per milestone; intermediate milestones are verified by reading the code and
building.

---

## M1 — The attribute and the shared predicate

**Files:** `libs/spark/MintPlayer.Spark.Abstractions/IgnorePropertyAttribute.cs` (new),
`libs/spark/MintPlayer.Spark.Abstractions/Reflection/ReflectedTypeExtensions.cs`

- `[AttributeUsage(AttributeTargets.Property)] public sealed class IgnorePropertyAttribute : Attribute;`
  — flat in the Abstractions root, matching `SortableAttribute`/`ReferenceAttribute`. XML doc states
  what it excludes (model JSON, mapping, includes, replication, generated `AttributeNames`) and that
  removal from a committed model file discards hand-authored settings on that attribute (PRD F2).
- Add to `ReflectedTypeExtensions`:
  - `bool IsSparkModelProperty(this PropertyInfo)` — `Name != "Id" && CanRead && CanWrite &&
    GetCachedCustomAttribute<IgnorePropertyAttribute>() is null`.
  - `IEnumerable<PropertyInfo> GetSparkModelProperties(this Type)` — `GetCachedProperties().Where(...)`.
  - `bool IsIgnoredForSparkModel(this PropertyInfo)` — the attribute check alone, for the union veto
    in M2 and the sites that need "ignored?" without the `Id`/accessor rules.
- Do **not** filter inside `GetCachedProperties()` (PRD D1).

**Verify:** builds; the predicate is the only place the rule is spelled out.

---

## M2 — ModelSynchronizer

**File:** `libs/spark/MintPlayer.Spark/Services/ModelSynchronizer.cs`

- Replace the three copied predicates with `GetSparkModelProperties()`: `:191-192`
  (`CollectEmbeddedTypes`), `:283-285` (collection), `:288-291` (projection).
- Apply the **union veto** (PRD D2): after building `allPropertyNames` (`:294-297`), subtract any
  name whose entity-side *or* projection-side `PropertyInfo` carries `[IgnoreProperty]`. Compute the
  ignored set from the *unfiltered* property lists so an entity-side ignore vetoes a
  projection-side survivor.
- `ComputeBreadcrumbProjectionSatisfiable` (`:532`) — use the filtered projection properties so an
  ignored property cannot satisfy a breadcrumb field.
- `ValidateBreadcrumb` (`:512-520`) — extend the message: if the missing placeholder names a CLR
  property that exists but is `[IgnoreProperty]`, say so explicitly (AC #9).
- Leave `IsComplexType` (`:663`) alone (PRD D3).

**Tests** (`tests/MintPlayer.Spark.Tests/Services/ModelSynchronizerTests.cs`, following the existing
temp-dir + `MS_`-prefixed top-level fixture pattern):
- ignored property absent from generated attributes (AC #1);
- pre-existing attribute removed on re-sync once ignored (AC #2);
- ignored complex property does not produce an embedded `{Type}.json` (AC #5);
- ignored property on an embedded type absent from that type's JSON (AC #4);
- entity-side ignore vetoes a projection-side property of the same name (D2);
- breadcrumb referencing an ignored property throws with the explanatory message (AC #9).

---

## M3 — Source generators

**Files:** `libs/source_generators/MintPlayer.Spark.SourceGenerators/Generators/PersistentObjectNamesGenerator.cs`,
`.../Diagnostics/ProjectionPropertyAnalyzer.cs`

- These are Roslyn, not reflection — they need their own symbol-level check
  (`GetAttributes().Any(a => a.AttributeClass?.Name is "IgnorePropertyAttribute" or "IgnoreProperty")`,
  matched by full metadata name where available). Add a small shared helper rather than duplicating
  the predicate in both files.
- `PersistentObjectNamesGenerator:53-62` — add to the existing filter chain so no constant is
  emitted (AC #8).
- `ProjectionPropertyAnalyzer:57-62` — skip ignored properties on **both** the entity and projection
  side, so no diagnostic fires for a property that is no longer modelled.

**Tests:** `tests/MintPlayer.Spark.SourceGenerators.Tests` — ignored property yields no constant;
analyzer stays silent on an ignored property whose types would otherwise mismatch.

---

## M4 — Runtime paths that bypass the schema

**Files:** `libs/spark/MintPlayer.Spark/Services/ReferenceResolver.cs`,
`.../Services/SyncActionHandler.cs`, `.../Services/LookupReferenceService.cs`

- `ReferenceResolver.GetReferenceProperties` (`:56-62`) and the projection fallback overload
  (`:66-86`) — filter through `IsSparkModelProperty()`, so an ignored `[Reference]` is never
  `.Include()`d (AC #6). Check the fallback pairs base-type attributes with projection
  `PropertyInfo`s (`:78`) — the veto must apply to the *entity-side* attribute there.
- `SyncActionHandler.BuildFromClrReflection` (`:135-151`) — the no-schema fallback; filter it.
- `LookupReferenceService.TransientToDto` (`:289-299`) — filter, so an ignored property on a
  transient lookup item stops shipping in the `Extra` dictionary.

**Tests:** `tests/MintPlayer.Spark.Tests` — ignored `[Reference]` produces no include path; the
reflection fallback omits the property.

---

## M5 — Replication

**Files:** `libs/replication/MintPlayer.Spark.Replication/Services/SyncActionInterceptor.cs`,
`libs/replication/MintPlayer.Spark.Replication.Abstractions/Models/SyncAction.cs`

- `HandleSaveAsync` (`:91-95`) — omit ignored properties from the `Data` dictionary.
- `GetPropertyNames` (`:186-192`) — omit from the `Properties` write-authorization list (PRD D5;
  security-relevant, AC #7).
- `SyncAction.EntityToDictionary` (`:99-104`) — omit from the `ToTransport()` payload.

**Tests:** ignored property appears in neither the transport payload nor the writable-property list.

---

## M6 — Close the raw-JSON write hole

**File:** `libs/spark/MintPlayer.Spark/Services/EntityMapper.cs` (`SetPropertyValue`, `:874-902`)

Per PRD D4/F5: add a `DefaultJsonTypeInfoResolver` modifier that drops properties carrying
`[IgnoreProperty]`, and use it in the `JsonSerializerOptions` this path deserializes with. Build the
options **once** as a static/cached instance — `JsonSerializerOptions` is expensive per-use and
caches its type-info internally.

**Tests:** posting a raw JSON object for a complex property does not write an ignored member on the
embedded type (AC #4, the security-meaningful half).

---

## M7 — Docs, demo, full sweep

- README: document `[IgnoreProperty]` alongside the other attributes, including the sync-removes-
  settings caveat (F2) and the orphan-embedded-file note (D8). **Done** — under "Model
  Synchronization", also calling out that `[JsonIgnore]` does not do this.
- Demo decoration: **skipped deliberately.** It would churn tracked model JSON (PRD F3) for no
  coverage the tests don't already give — the synchronizer, mapper, resolver, replication and
  generator paths are each pinned directly. Left out to keep the diff reviewable.
- Run the full suite: `dotnet test tests/MintPlayer.Spark.Tests/MintPlayer.Spark.Tests.csproj` plus
  the source-generator project. Re-run any failure in isolation before treating it as a regression
  (the suite is known to be flaky under parallel load).
- Open the PR against `master`.

---

## Risks

- **Order of milestones matters.** M2 removes attributes from the model; M3's generator change must
  land with it, or `AttributeNames` constants briefly outlive their attributes. Both ship in the
  same PR, so only intermediate commits are affected.
- **M6 is the one non-mechanical change.** If the resolver modifier proves invasive, the fallback is
  to gate the complex-object branch on the schema instead — but that is a larger behavioural change
  and should not be attempted without its own spike.
- **Demo JSON churn (F3)** — keep it to one property on one entity, or skip M7's demo step entirely,
  to keep the diff reviewable.
