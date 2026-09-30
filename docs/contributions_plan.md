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

### M0 — Spikes (answer before M2)
- [ ] **S-C1** Rights: can a user save only the contribution property without `Edit/<Target>`? Then
  owner **Q1**.
- [ ] **S-C2** List every entity-load path used for persistent-object work.
- [ ] **S-C3** AsDetail row identity for a slot-keyed element type.
- [ ] **S-C4** Optimistic concurrency in Spark's request session.
- [ ] **S-C5** Interceptor ordering with SoftDelete and Moderation.
- [ ] **S-C6** Measure that lazy `Load` + `LoadStartingWith` is one request.
- [ ] Record the results in the PRD (§4.1) and amend T5 and T6 where they differ.

### M1 — `TriggersRefresh` enum (C9)
- [ ] `ERefreshTrigger { None, Auto, ValueChanged, Blur }` with `JsonStringEnumConverter`.
  `EntityTypeDefinition.TriggersRefresh` becomes `ERefreshTrigger?`.
- [ ] C#: in `EffectiveObjectFactory.cs:64` and `SparkDevelopmentExtensions.cs:327`, the check becomes
  `!= None`. Add a verify-model warning for `Blur` on a discrete editor. Update the XML docs.
- [ ] TS:
  - `entity-type.ts:50` becomes a string union.
  - `refresh-coordinator.ts` gets `effectiveTrigger(attr)`, with `Auto` keeping today's type switch.
  - Add a debounce in `RefreshCoordinator` (300 ms; blur and save flush it).
  - Update the 5 reads in `spark-po-form.component.ts`.
- [ ] Rewrite `Car.json:403`, `CarreerJob.json:76` and `GateSettings.json:118` to `"Auto"`.
- [ ] Tests: `RefreshEndpointTests`, `NestedRefreshEndpointTests`, `SparkModelVerifyChecksTests`,
  `TriggersRefreshPreservationTests`, and the po-form specs. Add a `refresh-coordinator` spec for
  debounce and flush.
- [ ] Docs: `AGENTS.md:139,204,243`, `docs/guide-triggers-refresh.md`, `docs/Spark-API-Specification.md`.

### M2 — Framework seam (T5)
- [ ] `IPersistentObjectInterceptor.OnAfterMaterializeAsync(MaterializeContext)` with a default
  no-op. Call it on every S-C2 path.
- [ ] Add one test per path proving the hook ran and that AsDetail row rights see the hydrated rows.

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
- [ ] Emit `{Element}Contribution`, `{Element}Current`, the metadata class and the registration.
- [ ] Add the analyzer rules from T4, with code fixes: add `[JsonIgnore]`, change a slot type (a
  cross-project edit is allowed).
- [ ] Add Verify snapshots to `tests/MintPlayer.Spark.SourceGenerators.Tests`, plus diagnostic tests.

### M5 — Runtime (T6)
- [ ] `ContributionsInterceptor`: hydrate after materialize, diff and upsert or withdraw before save,
  recompute current on hide, delete or unhide, and retry on concurrency.
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

### M7 — Full sweep, docs, PR
- [ ] Write `docs/guide-contributions.md` (API, ids, the current-document cache, rights, moderation
  integration), and add the library to the README.
- [ ] Run all five test projects of the `.slnx` and the ng-spark vitest suites (logs to file).
- [ ] Check the version diff: minor bumps only.
- [ ] Open the PR, then hand off the MintPlayer consumer work (PRD §7).
