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

### M1c — Generic three-way conflict resolution (PRD §5 Q10) ✅
- [x] ng-spark `po-edit`: on a 409, re-fetch the PO and build a three-way per-attribute classification
  (base/mine/theirs). AsDetail lists are compared per row by `[ValueKey]`, recursing into rows changed
  on both sides. Row order follows theirs, with rows only I added appended.
- [x] A conflict dialog listing only true conflicts: Mine/Theirs per attribute with the normal
  renderers, keep-all-mine and take-all-theirs, who/when for `IAuditable`, and a "they changed …"
  list.
- [x] Rebase onto the fresh etag, with no auto-save.
- [x] Reuse or extract the normalization from History's `revision-diff.ts`.
- [x] Specs:
  - a pure merge function tested on the full matrix: scalar and AsDetail row cases, including
    delete-vs-edit and add-same-key
  - a po-edit spec for 409 → re-fetch → dialog → rebase → save
  - an E2E test where two browser contexts edit the same PO
- [x] Translations for the dialog texts (the existing `common.concurrencyConflict` key family).
- Follow-up (review of `98ab4641`):
  - [x] "Changed by" shows a user id. Resolve it to a name through History's existing
    `AddHistoryUserNameResolver` hook (a small server read, e.g. the last revision's resolved name).
    Done with no server change: the newest revision from `POST /spark/po/revisions` (take 1), asked
    only with `canViewHistory`, and taken only when it is the version merged against; the id stays as
    the fallback (the caller can read `ModifiedBy` anyway), and a failed lookup never blocks the flow.
  - [x] Render references by their label, not their id, in the dialog (an id → label map from both
    reads and the form's picker candidates, `po-edit/src/reference-labels.ts`).
  - [x] Render row conflicts with cells instead of a one-line summary (the row's visible attributes
    as `<spark-grid-cell>`s, mine beside theirs, differing ones marked; a removed side reads "(removed)").
  - [ ] Run the never-executed E2E selectors (`input#Model`, `.spark-conflict-dialog`) in M7.

### M2 — Framework seams (F1–F6)
- [x] **F1:** `OnAfterMaterializeAsync(MaterializeContext)` (default no-op, idempotent) at the
  `LoadManyAsync` site (after `:121`), the save reload (`:260`) and the `Before` load
  (`DatabaseAccess.cs:305`). Add the bypass warning for app `OnSaveAsync` overrides. One test per path.
  - As built: the pipeline hooks each entity *instance* once per request (reference set), so the
    Update pre-read's instance is not hooked again by the save reload; the `Before` side-session copy
    is a separate instance and is hooked. `MaterializeContext.Session` is typed `object`
    (Abstractions has no RavenDB reference; `GetSession()` in core returns it typed). Tests:
    `MaterializeInterceptorTests`.
- [x] **F2:** History revert and full sync never post satellite (`[Contribution]`) attributes. Add
  tests proving a revert or a sync does not withdraw contributions.
- [x] **F3:** Change SPARK017 to accept `[Contribution]` element types.
- [~] **F4:** superseded by attribute-level rights, **M2c** below (PRD §5, "Attribute-level rights,
  replacing F4"). No `Contribute` verb.

### M2c — Attribute-level rights in core (PRD §5 Q11–Q15) and the audit's leak fixes
Split into two sequential steps: **M2c-1** foundation (syntax, validator, SPARK014, effective table, stale-deny warning) and **M2c-2** enforcement plus leak fixes (red tests first).
Order: reproduce the existing leaks first (red tests), then build.
- [x] **Red tests** for leaks 1–4 and 6–7 in the PRD list: search oracle on a hidden string, Update
  echo of a protected value, breadcrumb token leak (own row and reference), sort/filter/distinct/count
  oracles, the shield on AsDetail/nested attributes, the create path.
  - Read side done (M2c-2a): leaks 1, 3 and 4 in `AttributeRightsEnforcementTests` (15 red → green).
    Leaks 2, 6 and 7 are write side: done in M2c-2b (`AttributeWriteEnforcementTests`, red → green).
- [x] **Syntax:** parse `{verb}/{Type}/{Attr}` everywhere rights are parsed. The combined verbs expand
  as prefixes. Custom-action rights with a third segment are refused.
  - The runtime validator refuses an unknown type or attribute at startup.
  - SPARK014 validates the attribute against the generated `AttributeNames`.
  - As built (M2c-1): `SparkAttributeRights` (Abstractions) — `IsAttributeAction` (Query/Read/Edit/New
    and combined verbs made only of them), `TrySplit`. `ResourcePattern.Parse` is unchanged: split on the
    first slash, an attribute right indexes as target `TYPE/ATTR`, so type-level probes are untouched.
    `SecurityConfigurationValidator.Validate(config, IModelLoader)` (the loader injects `IModelLoader`,
    which depends only on `IHostEnvironment` — no cycle) refuses a disallowed verb, a 4th segment, an
    unknown type (by name; aliases/reserved targets refused) or attribute. Hot reload: same path, so a
    bad edit throws on every read until fixed (fail closed, as for every rule — the previous file is
    not kept). SPARK014 is now an error; it checks the model JSON attributes ∪ the CLR type's public
    properties (the model is one build behind; a type with no model file yet is judged by its class).
    SPARK012 reads model files by position (`ModelNamesReader`), so an attribute/tab name is no longer
    a known type target.
- [x] **Evaluation:** compose type → type/attr with the four tiers into an effective (type, attr,
  verb) table, cached per request per type. The type right is required (no unlocking).
  - As built (M2c-1): `RightsDecision.ForAttributes(verb, type)` → `EffectiveAttributeRights`
    (`TypeAllowed`, `IsAllowed(attr)`, `Attributes` = mentioned attributes composed, `DeniedAttributes`,
    `IsUnrestricted`); a tier fires when it covers the type OR the attribute pattern. Exposed through
    `IAccessControl.GetAttributeRightsAsync` (default interface method: type decision inherited — test
    doubles keep working; `SecurityFileAccessControl` and the permissive test baseline override) and
    the scoped `IAttributeRights.GetEffectiveAsync(definition|name, verb)`, memoised per (type, verb),
    system context unrestricted. Tests: `AttributeRightsTests`.
- [x] **Enforcement — removal for static rights:**
  - PO GET/Refresh/New/custom-action results, and History presentation
  - the per-caller entity-type definition prune (`EntityTypes/Get|List`)
  - query columns (`QueryResultProjector.BuildColumns`)
  - search fields, sort, filter, distinct values, counts
  - As built (M2c-2a): `IAttributeRightsEnforcement` (scoped; denied sets memoised per (type,
    verb) over `IAttributeRights`; only *mentioned* attributes are ever removed). `PresentAsync`
    removes Read/Query-denied attributes (recursing into AsDetail rows by the row type's rights) and
    marks Edit/New-denied read-only — called by `po/load`, `po/refresh` (root and nested row; a
    nested row inside a Read-denied attribute is a 404), `po/new` (both paths), a custom action's
    PO result, `PersistentObjectPresenter` (History) and `RowSecurityGate` (every query path,
    streaming included). Not in `DatabaseAccess.GetPersistentObjectAsync`: that is the server-side
    read the write paths start from. `ForQueryAsync` gives the caller's query surface (a copy without
    Query-denied attributes) and `QueryExecutor` uses it for filters, search, sort, the in-memory
    sort, columns and distincts on both branches (and strips denied filters from a custom query's
    `args.Columns`); streaming builds its columns from it. `ForFormAsync` prunes `types/{id}` and
    `types` (Read-denied removed, Edit-denied `IsReadOnly` on a copied attribute —
    `EntityAttributeDefinition.ShallowCopy`), `DetailTypes` by their own rights.
    **Search narrowed** to attributes on the query surface (`ShowedOn.Query`, not Query-denied;
    `TranslatedString` `_lang` fan-out and `{Name}Search` companions kept), pushdown and in-memory
    fallback alike.
- [x] **Enforcement — write:**
  - a shield covering every attribute kind (scalars, references, multi-references, AsDetail,
    nested), driven by the effective table and the per-row hook
  - create drops posted non-New values
  - the Update response is re-presented (fixes the echo)
  - a History revert is partial and reported
  - As built (M2c-2b): `IAttributeWriteShield` (scoped), called by `IDatabaseAccess.SavePersistentObjectAsync`
    after every gate and before the interceptors, drops from the posted object every attribute the
    caller may not write: static `Edit` (`New` on a create) denials of the type, AsDetail rows by the
    row type's rights (`Edit` for a row claiming a stored row by key, `New` otherwise, recursing
    into embedded objects), and the per-row hook on the stored row (`Edit` always, `Read`-only unless
    `isValueChanged`; dotted names relative per AsDetail level). Dropped rather than restored: the
    mapper writes only present attributes, so the stored value (or the CLR default) stays for every
    kind without converting a stored value back to the wire — and no hook, interceptor or
    `OnSaveAsync` override sees a tampered value. The base `OnSaveAsync` keeps the per-row fallback
    only when constructed by hand. Static drops reach interceptors as `SaveContext.UnwritableAttributes`;
    History compares those paths between the revision and the entity and answers a partial revert
    with a warning notification. `ISaveResponsePresenter` makes `po/create`/`po/update` answer with
    the row re-read through `GetPersistentObjectAsync` plus the page-load presentation (falls back to
    the shielded posted object, Read-denied removed, when the caller may save but not read).
    Retry prompts are presented in the middleware's 449 path (`RetryPresentation`). Tests:
    `AttributeWriteEnforcementTests`, `AttributeVerbMatrixTests`, `HistoryTests` (partial revert).
- [x] **Per-row hook:** blanking indistinguishable from empty (drop the `IsVisible` flip), plus
  breadcrumb/`po.Name` token blanking for own and reference targets.
  - Token blanking done (M2c-2a): `BreadcrumbResolver` renders a token empty when a static right
    refuses it (roots under the gate's verb, references under Read, AsDetail row types under their
    owner's) or the per-row hook protects it on that document (`IRowSecurity.GetProtectedAttributesAsync`;
    projected roots judged on their base document, one batched load; missing → no field token).
    `BreadcrumbResult.DeniedTokensByType` carries the static half to `EmbeddedBreadcrumbRenderer`.
    The `IsVisible` flip is dropped (M2c-2b): `RowSecurity.RedactAttribute` only empties the value, so a
    blanked attribute serialises exactly like an empty one; `po/refresh` asks the hook for the names
    instead of reading the old `IsVisible` delta; an embedded row's own breadcrumb blanks a column its
    owner's hook protects under a dotted name (`BreadcrumbResult.BlankedTokensById`).
- [x] **Stale-deny warning** in the analyzer and the security-posture report.
  - As built (M2c-1): SPARK024 (warning) and `SecurityPosture.Notes` (logged at Information) via
    `StaleAttributeDenials`: per (group, verb, type) with ≥1 attribute denial, the attributes no
    attribute right of that group mentions for that verb. The anonymous surface no longer lists an
    attribute grant whose type right is unreachable.
- [x] **Tests:**
  - a reflection-driven "every attribute kind" test (test-only)
  - a verb matrix: Query/Read/Edit/New × type/attr × allow/deny/important, with Delete and custom
    actions staying type-level
  - the M1c dialog never shows a removed attribute (M2c-2a: pinned server side — the dialog's
    re-fetch is `po/load`, and `Get_omits_a_Read_denied_attribute` asserts the payload omits it)
  - the contributor scenario: `Edit/Song` plus denies, and `Edit/Lyrics/Text`
- [x] **F5:** interceptor ordering (`Order` or a startup check).
  - As built: `int Order` default-implemented on the interface; scale in
    `PersistentObjectInterceptorOrder` (SoftDelete -300, History -200, Moderation -100, default 0,
    Contributions +100); ties keep registration order. Satellite (F2) = Newtonsoft `[JsonIgnore]`,
    `ReflectedTypeExtensions.IsSparkSatelliteProperty` / `GetSparkSatellitePropertyNames`.
- [x] **F6:** evict interceptor-stored documents when a save or delete is refused. Test: a refused
  save followed by a second save in the same request commits no orphaned documents.
  - As built: `SessionWriteSnapshot` records the request session's tracked entities (and which were
    already dirty or deleted) before the Actions class and the before-hooks run, and a refusal or a
    `ConcurrencyException` evicts every entity tracked, changed or deleted since — on save, single
    delete (with and without interceptors) and bulk delete. Changes pending before the operation are
    left alone. Not covered: raw deferred commands and `Delete(id)` of an unloaded document. Tests:
    `RefusedWriteEvictionTests`.
- [x] **Reserved verbs (Q2):** `[assembly: SparkReservedActions(typeof(X))]` (Abstractions), declared
  by core (`SparkCoreActions` + `SparkCombinedActions`), SoftDelete, History and Moderation
  (`[SparkNotAnAction]` excludes `ModerationRights.Target`). SPARK023 (error) refuses a custom action
  named like a reserved verb, read from the compilation and its references; SPARK011's built-in and
  combined lists are derived from the same declarations; `UseSpark()` refuses the same collision at
  startup (`SparkReservedActionRegistry`). Moderation's `DefaultEarnable` uses core's constants and
  `--spark-init-moderation`'s per-type verb list comes from the registry. `Contribute` is not declared
  yet (F4 on hold). Tests: `ReservedActionNameAnalyzerTests`, `SecurityConfigurationAnalyzerTests`,
  `SparkReservedActionRegistryTests`.

### M2d — Follow-ups from the M2c-2b review (`07c62c54`)
- [x] **Natural-id probe sees New-denied posted values** (`DatabaseAccess` natural-id probe ~:257
  runs before the write shield). A caller can influence a natural id derived from an attribute they
  may not set, and the collision answer is an existence oracle. Fix: run the shield (or at least drop
  New/Edit-denied values) before the probe and before `ToEntity` for the probe. Start with a red test.
- [x] Decide whether validation should run on the shielded values. Today a *required* attribute
  blanked by the per-row hook fails "required" on save; it was documented as a known limitation in
  M2c-2b. Fix it if it's cheap, since validation of values the user can't set is meaningless.
  **Done:** validation runs inside the save after the shield (`ISaveValidation`) and skips what the
  caller may not write; the create shield runs before the natural-id probe. An update never moves a
  natural id. Tests: `AttributeWriteProbeAndValidationTests`.

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
- [ ] M1c integration: in the conflict merge, contribution rows (the `[Contribution]` property) only
  conflict between two edits by the same user. Another user's change to a slot is theirs-wins with a
  notice, because it is their own contribution document.
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
