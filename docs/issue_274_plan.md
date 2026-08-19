# Plan — Issue #274: preserve hand-edited `showedOn` on synchronize

**PRD:** [issue_274_PRD.md](issue_274_PRD.md) ·
**Branch:** `fix/issue-274-preserve-showedon`

Test-first by request: M1 lands the failing tests (red), M2 makes them pass. The PR is squashed, so
the intermediate red commit never reaches `master`.

| | Milestone | Commit |
|---|---|---|
| S0 | Spike: reproduce the wipe on the Fleet demo | — (no commit; result recorded below) |
| M1 | Failing tests pinning the preserve contract | |
| M2 | The fix: intersect `ShowedOn` with structural capability | |
| M3 | Docs, demo zero-diff verification, follow-up issues, lockstep version bump | |

---

## S0 — Spike: reproduce on Fleet (no commit)

Hand-edit `Demo/Fleet/Fleet/App_Data/Model/Car.json`: narrow one dual-present attribute's
`"showedOn": "Query, PersistentObject"` to `"PersistentObject"`. Run
`dotnet run --project Demo/Fleet/Fleet -- --spark-synchronize-model`, diff. Expected on `master`:
the edit is reverted (confirms the unit-level analysis holds in the real pipeline — registry,
`[GenerateIndex]` output and all). Revert the working tree afterwards. Record the result in the PRD.

## M1 — Failing tests (PRD R1, R3–R6; AC 1, 4)

**Files:** `tests/MintPlayer.Spark.Tests/Services/ModelSynchronizerTests.cs`,
`tests/MintPlayer.Spark.Tests/Model/SynchronizeIdempotencyTests.cs`

Fixture pattern: the existing NSubstitute `IIndexRegistry` stub —
`_indexRegistry.GetRegistrationForCollectionType(typeof(X)).Returns(registration)` with a real
`IndexRegistration` carrying a `ProjectionType` (template at `ModelSynchronizerTests.cs:328-372`).
Tamper-on-disk pattern: `MultiLineString_dataType_is_preserved_on_re_synchronize` (`:216`) — sync,
regex-edit the JSON, sync again, assert.

Tests (names indicative):

1. `Hand_trimmed_ShowedOn_on_dual_present_attribute_survives_re_synchronize` — **the #274 repro**:
   sync a projected entity, edit a dual-present attribute's `showedOn` to `"PersistentObject"` on
   disk, sync again, assert it is still `PersistentObject`. RED on master.
2. `Hand_trimmed_ShowedOn_reaches_a_fixed_point` (idempotency suite) — after the tamper, run 2 and
   run 3 produce byte-identical files. RED on master.
3. `Attribute_leaving_the_projection_loses_the_Query_flag` — dual-present with `showedOn` both;
   remove the property from the projection type; re-sync narrows to `PersistentObject` (R3). Green
   today; pins that the fix keeps structural narrowing.
4. `ShowedOn_with_no_valid_side_self_heals_to_capability` — hand-set `"Query"` on a
   collection-only attribute; re-sync yields the derived capability (`PersistentObject`), not an
   empty flag set (R5). RED on master only in the sense that master widens instead of healing —
   assert the exact healed value.
5. `Adding_a_projection_to_an_existing_entity_still_narrows_single_sided_attributes` — entity
   synced without projection (all `showedOn` both), then registry starts returning a registration:
   collection-only attribute narrows to `PersistentObject`, dual-present stays both (R4). Green
   today; guards the CodeCoverage-adoption path.
6. `Plain_entity_ShowedOn_is_untouched_on_re_synchronize` — no projection; hand edit survives (R6).
   Green today.

Commit M1 with tests 1, 2 and 4 failing — that is the point.

No new E2E test: `MintPlayer.Spark.E2E.Tests` is an HTTP/security suite against a running Fleet
host; synchronize is an offline builder-phase concern. The end-to-end layer for this fix is the
idempotency suite (test 2) plus the demo zero-diff sweep in M3.

## M2 — The fix (PRD R1–R6, D1)

**File:** `libs/spark/MintPlayer.Spark/Services/ModelSynchronizer.cs:591`

Replace the unconditional overwrite with the intersection (PRD D1):

```csharp
// ShowedOn is presentation constrained by structure: projection/entity membership is
// the capability to appear on a side, the model author picks the subset. Strip sides
// that structurally disappeared, never re-add one (#274). An empty result self-heals
// to the derived capability.
var narrowed = existingAttr.ShowedOn & showedOn;
existingAttr.ShowedOn = narrowed != 0 ? narrowed : showedOn;
```

Create path (`:629`) and the plain-entity `else` branch stay untouched. All M1 tests green; the
whole `MintPlayer.Spark.Tests` suite must pass unchanged — the audit says no existing assertion
covers `ShowedOn`, so any other failure means the change leaked wider than intended.

## M3 — Docs, verification, follow-ups, version bump (PRD R7; AC 2, 3)

- `libs/spark/MintPlayer.Spark/README.md`: extend the synchronize-preservation section — `showedOn`
  is preserved on projected entities; synchronize only strips structurally impossible sides.
- Re-run `--spark-synchronize-model` on all four demo apps; assert **zero git diff** (R7). Dual-
  present attributes already store the derived "both", so intersection is a no-op there.
- Run the full test suite once, at the end (per working convention).
- File the two follow-up issues from the PRD (the `Query` wipe at `:575`; SparkQuery
  `Source`/`IndexName`/`UseProjection` staleness) and link them in the PRD.
- Lockstep version bump: all 21 packable libs `10.0.0-preview.53` → `10.0.0-preview.54`.
- Update PRD/plan status, commit, open the PR.

## Verification

- New tests 1, 2, 4 confirmed RED on master (S0 + M1 run before M2).
- Full `MintPlayer.Spark.Tests` suite green after M2. If a full-suite run flakes under load,
  re-run the named tests in isolation before calling it a regression (known flake).
- Demo model zero-diff sweep (AC 3).
