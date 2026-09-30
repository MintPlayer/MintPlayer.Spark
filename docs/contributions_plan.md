# Plan — `MintPlayer.Spark.Contributions`

Requirements, decisions (C1–C10, T1–T7), spikes and open questions are in
[contributions_PRD.md](contributions_PRD.md). This file is the order of work.

**Rules for executing this plan**
- **Commit per milestone.** Do not run test suites per milestone; verify with a build and by reading
  the code. There is one full sweep at the end (M7).
- **Logs** go raw to the scratchpad (`cmd > x.log 2>&1; echo "EXIT: $?"`).
- **Versions.** Every touched `libs/` csproj gets a minor bump (11.x preview), and ng-spark bumps minor
  (22.x). **No major changes.**
- **No backward compatibility** (C10).

---

### M0 — Spikes ✅ (2026-09-30; results in PRD §4.1, required additions F1–F7 in §4.2)
- [x] S-C1 to S-C6
- [ ] Second grilling round (owner Q1 onwards), then amend C/T decisions

### M1b — Core concurrency fixes (F7) ✅
- [x] Add an HTTP regression test first: a concurrent PO save, and what a raw `ConcurrencyException`
  returns.
- [x] Make the PO save write with the checked change vector, and map `ConcurrencyException` to the
  409 envelope without leaking change vectors.

### M1 — `TriggersRefresh` enum (C9)
- [x] `ERefreshTrigger { None, Auto, ValueChanged, Blur }` with `JsonStringEnumConverter`.
  `EntityTypeDefinition.TriggersRefresh` becomes `ERefreshTrigger?`.
- [x] C#: in `EffectiveObjectFactory.cs:64` and `SparkDevelopmentExtensions.cs:327`, the check becomes
  `!= None`. Add a verify-model warning for `Blur` on a discrete editor. Update the XML docs.
- [x] TS:
  - `entity-type.ts:50` becomes a string union.
  - `refresh-coordinator.ts` gets `effectiveTrigger(attr)`, with `Auto` keeping today's type switch.
  - Add a debounce in `RefreshCoordinator` (300 ms; blur and save flush it).
  - Update the 5 reads in `spark-po-form.component.ts`.
- [x] Rewrite `Car.json:403`, `CarreerJob.json:76` and `GateSettings.json:118` to `"Auto"`.
- [x] Tests: `RefreshEndpointTests`, `NestedRefreshEndpointTests`, `SparkModelVerifyChecksTests`,
  `TriggersRefreshPreservationTests`, and the po-form specs. Add a `refresh-coordinator` spec for
  debounce and flush.
- [x] Docs: `AGENTS.md:139,204,243`, `docs/guide-triggers-refresh.md`, `docs/Spark-API-Specification.md`.

### M1c — Generic three-way conflict resolution (PRD §5 Q10)
- [ ] ng-spark `po-edit`: on a 409, re-fetch the PO and build a three-way per-attribute classification
  (base/mine/theirs). AsDetail lists are compared per row by `[ValueKey]`, recursing into rows changed
  on both sides. Row order follows theirs, with rows only I added appended.
- [ ] A conflict dialog listing only true conflicts: Mine/Theirs per attribute with the normal
  renderers, keep-all-mine and take-all-theirs, who/when for `IAuditable`, and a "they changed …"
  list.
- [ ] Rebase onto the fresh etag, with no auto-save.
- [ ] Reuse or extract the normalization from History's `revision-diff.ts`.
- [ ] Specs:
  - a pure merge function tested on the full matrix: scalar and AsDetail row cases, including
    delete-vs-edit and add-same-key
  - a po-edit spec for 409 → re-fetch → dialog → rebase → save
  - an E2E test where two browser contexts edit the same PO
- [ ] Translations for the dialog texts (the existing `common.concurrencyConflict` key family).

### M2 — Framework seams (F1–F6)
- [ ] **F1:** `OnAfterMaterializeAsync(MaterializeContext)` (default no-op, idempotent) at the
  `LoadManyAsync` site (after `:121`), the save reload (`:260`) and the `Before` load
  (`DatabaseAccess.cs:305`). Add the bypass warning for app `OnSaveAsync` overrides. One test per path.
- [ ] **F2:** History revert and full sync never post satellite (`[Contribution]`) attributes. Add
  tests proving a revert or a sync does not withdraw contributions.
- [ ] **F3:** Change SPARK017 to accept `[Contribution]` element types.
- [ ] **F4 (per Q1):** the contribute-only save path, with shielding supplied by a registry and the
  row and disabled-action gates judging `Contribute`. Tests: a contributor can't change the title,
  tampered `IsValueChanged` flags are ignored.
- [ ] **F5:** interceptor ordering (`Order` or a startup check).
- [ ] **F6:** evict interceptor-stored documents when a save or delete is refused. Test: a refused
  save followed by a second save in the same request commits no orphaned documents.

### M3 — Library skeleton and packaging (C5, C7)
- [ ] Create `libs/contributions/MintPlayer.Spark.Contributions` and `.Abstractions`: the
  `[Contribution]` and `[ContributionSlot]` attributes, and the `IContribution`,
  `ICurrentContribution`, `IContributions` and `IContributionValidator<T>` interfaces.
- [ ] Create `MintPlayer.Spark.Contributions.SourceGenerators` (`netstandard2.0`, `IsRoslynComponent`,
  `IsPackable=false`, Roslyn 5.9.0 pins).
- [ ] Pack it into `analyzers/dotnet/cs` following `AllFeatures.csproj:40-65`, and add a
  `buildTransitive` targets file that wires the analyzer `ProjectReference` in-repo.
- [ ] Decide whether `MintPlayer.SourceGenerators.Tools.dll` is needed at run time; if so, embed it.

### M4 — Generator and analyzer (T2, T3, T4)
- [ ] Use a `ForAttributeWithMetadataName` pipeline on `[Contribution]` properties. Unwrap
  `T`/`T[]`/`List<T>`/`IList<T>`/`IReadOnlyList<T>`/`ICollection<T>`/`IEnumerable<T>` and read the
  `[ContributionSlot]` properties from symbols (works across assemblies).
- [ ] Emit `{Target}{Property}Contribution`, `{Target}{Property}Current` (Q9: always target+property, no override), the metadata class and the registration.
- [ ] Add the analyzer rules from T4, with code fixes: add `[JsonIgnore]`, change a slot type (a
  cross-project edit is allowed).
- [ ] Add Verify snapshots to `tests/MintPlayer.Spark.SourceGenerators.Tests`, plus diagnostic tests.

### M5 — Runtime (T6)
- [ ] `ContributionsInterceptor`:
  - Hydrate (F1), with a lazy prefix load ending in `/` and an explicit `pageSize`.
  - Diff before-save by slot, and upsert or withdraw only the current user's own documents.
  - Check Moderation suspension and locks itself, because its documents aren't PO saves.
  - Recompute in **before-delete** (registered after SoftDelete) and on Restore in before-save, never
    after-commit.
  - Pin the current document's change vector (update: cv, create: `""`, delete: cv). Retry up to 3
    times, then return 409.
- [ ] Generator: emit the get-only `[ValueKey] Key` built from the slot tuple, plus its
  `SparkValueObjects.Register` module initializer (F3).
- [ ] `IContributions.RebuildCurrentAsync(targetId)`.
- [ ] Add `AddContributions()` and wire it into AllFeatures, if AllFeatures lists every feature.

### M6 — Demo and tests
- [ ] A demo consumer in an existing app (DemoApp, or the QnA sample): a target with a
  `[Contribution]` list of a two-slot element.
- [ ] Integration tests (RavenTestDriver), covering R2–R5 and R8:
  - one request on load (`NumberOfRequests`)
  - the target is unmodified when only contributions changed
  - owner-only writes, including tampering with the id or `ContributorId`
  - a hide makes the next-latest current, immediately
  - two contributors racing on one slot
  - withdrawing a contribution
  - rebuild
  - a slot value that fails validation
- [ ] An E2E test on the demo form: add a version, edit it, remove it.

### Later — MintPlayer lyrics timings (look-ahead only, NOT scheduled; see PRD §9)
Recorded so that the Contributions design covers it. It is executed in the MintPlayer repository
after this library ships, and after MintPlayer's F9 messaging.
- [ ] Stable `Guid` ids and a stored duration for media, replacing URL-keyed timings.
- [ ] Canonical timeline (in ms, on a reference medium, timed on the original version), a segment
  time map per medium, and sparse overrides. All of them are contributions (PRD §9.2).
- [ ] Manual tooling:
  - tap mode, playback rate, nudge, shift-from-here, latency compensation, preview/loop, undo, LRC
    import/export
  - a public `seek`/`setRate` in `@mintplayer/ng-video-player`
- [ ] Anchor alignment for additional media (2–3 taps → segment map).
- [ ] LRCLIB seed lookup, after the licence question (L6) is settled.
- [ ] Spikes L1–L6, then decide on the optional `mintplayer-aligner` worker (Demucs + WhisperX or
  similar, audio uploaded by an admin and deleted after processing, results stored as system-user
  contributions for human review).

### M7 — Full sweep, docs, PR
- [ ] Write `docs/guide-contributions.md` (API, ids, the current-document cache, rights, moderation
  integration), and add the library to the README.
- [ ] Run all five test projects of the `.slnx` and the ng-spark vitest suites (logs to file).
- [ ] Check the version diff: minor bumps only.
- [ ] Open the PR, then hand off the MintPlayer consumer work (PRD §7).
