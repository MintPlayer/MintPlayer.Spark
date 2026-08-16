# Plan — Issue #253 part 2: preserve model attributes, emit get-only properties

**PRD:** [issue_253_PRD.md](issue_253_PRD.md) ·
**Branch:** `feat/issue-253-preserve-model-attributes`

Milestones are ordered so the riskiest change (M2) lands on top of a green, independently shippable
fix (M1). One commit each.

**All milestones complete.** PR [#263](https://github.com/MintPlayer/MintPlayer.Spark/pull/263), CI green.

| | Milestone | Commit |
|---|---|---|
| M1 | Preserve attributes with no CLR property + log | `194532b` |
| M2 | Split the property filter; emit get-only as `IsReadOnly = true` | `cef2e72` |
| M3 | Exclude indexers | `ee64a9c` |
| M4 | Docs, follow-up issues, lockstep version bump | `7e4033e` |

---

## M1 — Stop dropping attributes (PRD R1, R2, R3, R4, R8)

**Files:** `libs/spark/MintPlayer.Spark/Services/ModelSynchronizer.cs`

After the rebuild loop and before `entityTypeDef.Attributes = newAttributes.ToArray()` (`:481`),
append every entry from `existingAttrs` whose `Name` was not rebuilt, preserving original relative
order (PRD D3).

The one subtlety that decides correctness: **an attribute vetoed by `[IgnoreProperty]` must still be
dropped** (PRD F1/R3). `ignoredPropertyNames` is already computed at `:296-300`, so the leftover set is

> everything in `existingAttrs` that is neither in `allPropertyNames` nor in `ignoredPropertyNames`.

Log once per preserved orphan, naming the attribute, the type, and what to do:
`"Attribute 'X' on 'Y' has no matching CLR property — kept. Remove it from the model JSON if obsolete."`

**Tests** (`tests/MintPlayer.Spark.Tests/Services/ModelSynchronizerTests.cs`):
- A hand-authored attribute with no CLR property survives a round-trip with `Id`, `Label`, `Renderer`,
  `RendererOptions`, `Group`, `EditMode` and `Rules` **unchanged** — this is the virtual-attribute case
  the user described, and the `Id` assertion is the one that matters most.
- Removing a property from the class preserves its attribute rather than dropping it (PRD F1 notes
  **no test exists for this today** — it is the actual target scenario).
- Renaming a property yields old + new side by side, with the old one's `Renderer`/`Rules` intact.
- `Re_synchronize_removes_an_attribute_that_has_become_ignored` (`:431`) and
  `Ignoring_a_property_on_the_entity_vetoes_the_same_name_on_the_projection` (`:482`) **still pass
  unchanged.** If either needs editing, the change is wrong.
- Attributes that do have properties keep today's field-preservation exactly (R4).

Independently shippable. If M2 proves troublesome, M1 ships alone.

## M2 — Split the filter, emit get-only properties (PRD R5, R6, D2)

**Files:** `libs/spark/MintPlayer.Spark.Abstractions/Reflection/ReflectedTypeExtensions.cs`,
`libs/spark/MintPlayer.Spark/Services/ModelSynchronizer.cs`,
`libs/replication/MintPlayer.Spark.Replication/Services/SyncActionInterceptor.cs`

**Do the filter split first, as its own step, before changing what the model admits.** Widening the
shared predicate and fixing the call sites afterwards would leave replication's write-authorization
list transiently wrong (PRD F5) — a security-adjacent regression that a passing build would not catch.

1. Add `GetSparkWritableProperties()` / `IsSparkWritableProperty` — today's exact
   `CanRead && CanWrite && !ignored` semantics.
2. Move `SyncActionInterceptor.GetPropertyNames` (`:197-204`) to it. **No behaviour change** — verify
   by running the replication suite before step 3.
3. Only then relax `IsSparkModelProperty` to `CanRead && !ignored` (dropping `CanWrite`).
4. `ModelSynchronizer` create branch (`:462`): `IsReadOnly = !property.CanWrite` instead of hardcoded
   `false`.
5. Leave the update branch alone — `IsReadOnly` is preserved for existing attributes (PRD R4), so a
   hand-set value is not stomped.

Review `SyncActionHandler.cs:152` (`BuildFromClrReflection`): outbound-only, so read-only properties
are expected to be benign — confirm rather than assume.

**Tests:**
- A get-only property produces an attribute with `IsReadOnly = true`.
- A hand-set `IsReadOnly` on an existing attribute survives re-synchronize.
- A get-only **complex** property generates its embedded model file (PRD D4).
- **A get-only property does NOT appear in replication's write-authorization list** — the R6 guard, and
  the most important test in this milestone.
- Existing `ModelSynchronizer` and replication suites unchanged.

## M3 — Exclude indexers (PRD R7, F6)

**File:** `libs/spark/MintPlayer.Spark.Abstractions/Reflection/ReflectedTypeExtensions.cs`

Add `GetIndexParameters().Length == 0` to both predicates. Test: an entity with `this[int]` produces
no `"Item"` attribute. Latent bug — no production impact expected, closing it while in the file.

## M4 — Docs and follow-ups

- `libs/spark/MintPlayer.Spark/README.md` — hand-added attributes survive synchronize; get-only
  properties become read-only attributes; restate `[IgnoreProperty]` (out of model) vs `[JsonIgnore]`
  (out of document).
- Update `IgnorePropertyAttribute`'s doc comment if its description of removal semantics reads as a
  general claim rather than one specific to the ignore path.
- **File two follow-up issues** (PRD "Out of scope"): the post-load actions hook, and the Get/List
  projection asymmetry. Reference them from the PRD.
- Lockstep version bump across all 21 packable libs.
- Record in the PRD that `--prune-orphaned-attributes` was considered and rejected (D1), so the next
  person does not re-litigate it from scratch.

## Verification — results

**1487 tests pass** (1389 + 60 + 38). CI green on the first run.

Every new test was confirmed to **fail without its fix**, not merely to pass with it:

- M1's three tests fail when `ModelSynchronizer.cs` is reverted.
- M3's indexer test fails when the `GetIndexParameters()` guard is removed.
- Pointing replication's write-authorization list back at the model filter — the exact mistake M2
  exists to prevent — fails **3 tests**, two of which predate this branch.

The planned demo-app diff **found nothing to inspect**: no entity in `Demo/` or `libs/` declares a
get-only property, so M2 changes no generated output there. The generated JSON for a computed property
was inspected directly instead, and its shape (`dataType`, `isReadOnly`, `isRequired`, `isVisible`) is
now pinned by assertions rather than by having been eyeballed once.
