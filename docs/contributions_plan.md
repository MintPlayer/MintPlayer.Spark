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

### M3 — Library skeleton and packaging (C5, C7) ✅
- [x] Create `libs/contributions/MintPlayer.Spark.Contributions` and `.Abstractions`: the
  `[Contribution]` and `[ContributionSlot]` attributes, and the `IContribution`,
  `ICurrentContribution`, `IContributions` and `IContributionValidator<T>` interfaces.
  **As built:** Abstractions is `net11.0` and references `MintPlayer.Spark.Abstractions` (for
  `SparkReservedActions` and `ValidationError`), like Moderation.Abstractions. Every public type is in
  namespace `MintPlayer.Spark.Contributions` although it lives in the Abstractions assembly, because
  SPARK017 matches `MintPlayer.Spark.Contributions.ContributionAttribute` by metadata name. Also there:
  `ContributionAttribution` (`[Flags]` None/Contributor/UpdatedAt/History, Q6) and `ContributionRights`
  (`RevertContribution` only, declared via `[assembly: SparkReservedActions]`). `IContributions` has
  just `RebuildCurrentAsync(targetId, ct)`; `IContributionValidator<in T>.ValidateAsync(targetId,
  element, ct)` returns `IReadOnlyList<ValidationError>`. `AddContributions()` registers a placeholder
  `IContributions` that throws `NotSupportedException` (runtime in M5). No AGENTS.md: no optional
  sibling library has one.
- [x] Create `MintPlayer.Spark.Contributions.SourceGenerators` (`netstandard2.0`, `IsRoslynComponent`,
  `IsPackable=false`, Roslyn 5.9.0 pins). **As built:** `ContributionsGenerator` runs a
  `ForAttributeWithMetadataName` pipeline and emits nothing yet.
- [x] Pack it into `analyzers/dotnet/cs` following `AllFeatures.csproj:40-65`, and add a
  `buildTransitive` targets file that wires the analyzer `ProjectReference` in-repo. **As built:**
  `Targets/spark-contributions.targets` adds the reference only when the generator csproj exists next
  to it, so a package consumer gets no dangling reference (MSB9008). It leaves SPARK001-003 alone: the
  package does not carry `MintPlayer.Spark.SourceGenerators.dll`, so it must not set
  `MintPlayerSparkSourceGeneratorsReferenceValidated`.
- [x] Decide whether `MintPlayer.SourceGenerators.Tools.dll` is needed at run time; if so, embed it.
  **Decided: not referenced.** The generator builds on plain Roslyn, so the nupkg embeds exactly one DLL.
- **AllFeatures is unchanged.** It references only Spark, Authorization, Messaging, Replication, Cron and
  Migrations, not every optional feature (SoftDelete, History and Moderation are absent), so
  Contributions stays out as well. M5's "wire into AllFeatures if it lists every feature" therefore
  does not apply.
- Test: `ContributionsGeneratorTests` (the generator runs over a `[Contribution]` property with no
  output and no diagnostics).

### M4 — Generator and analyzer (T2, T3, T4) ✅
- [x] Use a `ForAttributeWithMetadataName` pipeline on `[Contribution]` properties. Unwrap
  `T`/`T[]`/`List<T>`/`IList<T>`/`IReadOnlyList<T>`/`ICollection<T>`/`IEnumerable<T>` and read the
  `[ContributionSlot]` properties from symbols (works across assemblies).
  **As built:** one `ContributionInspector` reads a declaration for both the generator and the
  analyzer (base-type properties first, declaration order; a value property is any public instance
  property with a getter and a `set`/`init`). The generator reports nothing and emits nothing for a
  declaration with a blocking error. The owner need not be `partial` (nothing is added to it); the
  element must be, whenever the generator adds something (slots → `Key`, or attribution), and must be
  in the **same compilation** — an element from another assembly is SPARK032 and generates nothing,
  because a generator can only add members to its own compilation.
- [x] Emit `{Target}{Property}Contribution`, `{Target}{Property}Current` (Q9: always target+property, no override), the metadata class and the registration.
  **As built:** both documents are `partial`, carry `string? Id` and implement `IHasNaturalId`
  explicitly over a static `GetId`. `Current` also carries `TargetId` (its explicit `GetId()` needs
  it; T3 did not list it). `ContributionCount` is on `Current` only with `History`. The contribution
  type is `ISoftDeletable` (public members) when `MintPlayer.Spark.SoftDelete.ISoftDeletable`
  resolves, unless the app's own partial already implements it. The metadata class
  `{Target}{Property}ContributionMetadata` derives from the new `ContributionDescriptor<TTarget,
  TElement, TContribution, TCurrent>` (Abstractions): prefixes, slot keys, `FindInvalidSlot`, ids,
  row get/set, the element ↔ contribution ↔ current mappings, `Shape` (FNV-1a 64 over target,
  property, element, slots, values, attribution and soft delete) and `QueryName`. **Registration:**
  one `[ModuleInitializer]` per assembly (`{RootNamespace}.SparkContributionsRegistration`) calls
  `SparkValueObjects.Register(typeof(E), "Key", …)` per collection element and
  `ContributionRegistry.Register(…Metadata.Instance)` per declaration; M5's `AddContributions()`
  reads `ContributionRegistry.Descriptors` (no scanning; it must make sure the declaring assemblies
  are loaded, e.g. through the model's entity types). The element partial (once per element, the
  attribution unioned over its declarations) carries the get-only `[ValueKey] Key` (S-C3 / F3 — the
  M5 item below is therefore done here) and the read-only attribution properties over `internal`
  fields that `CreateRow` sets. Slot formatting is the shared `ContributionSlotFormat` (Abstractions):
  invariant integers, enum names, `Guid` as `N` (32 chars — `D` would exceed the 32-character segment),
  `bool` as `true`/`false`, null as empty (refused by `IsValid`). The lazy-load helper of T3 is
  **not** generated: the descriptor lives in domain projects that need not reference the RavenDB
  client, so M5 does the lazy prefix load generically from `CurrentPrefix` and `CurrentType`. The
  `contributionAttribution` rendering hint is a constant (`ContributionDescriptor.AttributionRenderingHint`)
  plus `AttributionAttributeNames`; a C# property cannot set a model renderer, so M5 applies it to
  the model.
- [x] Add the analyzer rules from T4, with code fixes: add `[JsonIgnore]`, change a slot type (a
  cross-project edit is allowed).
  **As built:** SPARK025 slot type (error), SPARK026 cardinality (error), SPARK027 no values (error),
  SPARK028 owner without a public `string Id` (warning — the detectable "PO entity" rule), SPARK029
  no Newtonsoft `[JsonIgnore]` (error, **fix**), SPARK031 element not partial (error, **fix**, also
  makes containing types partial, resolved solution-wide by metadata name), SPARK032 element in
  another assembly (warning), SPARK033 SoftDelete not referenced (warning), SPARK034 unsupported
  shape (error), SPARK035 clash with generated members / `[ValueKey]` / `[ValueObject]` (error).
  SPARK030 stays the Authorization MSBuild warning. **No slot-type fix:** there is no type to pick for
  the author.
- [x] Add Verify snapshots to `tests/MintPlayer.Spark.SourceGenerators.Tests`, plus diagnostic tests.
  **As built:** `ContributionsGeneratorTests` (4 snapshots: Song/Lyrics with attribution None and no
  SoftDelete, with all attribution and SoftDelete, int/enum/Guid/bool slots on an array, single-valued
  without slots; record element; app-owned `ISoftDeletable`; blocked declaration; compile-and-run of
  the PRD ids, key, registry and mappings, invariant formatting under `sv-SE`) and
  `ContributionsAnalyzerTests` (every rule, both fixes, the analyzer silent on generated members).
  The test project now references `MintPlayer.Spark.SoftDelete.Abstractions`.

### M5a — Runtime core (T6) ✅
- [x] `ContributionsInterceptor` (`Order` = Contributions, +100), one typed `ContributionHandler<…>` per
  descriptor (`ContributionCatalog`, `MakeGenericType` once per declaration):
  - [x] Hydrate (F1) on every reason, in the materializing session: one lazy `LoadStartingWith` over
    the current prefix (ending in `/`, `pageSize` 1024; a single-valued property is a lazy point load).
  - [x] Diff before-save by slot (Save/New only, and only when the attribute was posted and is writable),
    by value (JSON of the generated mapping); upsert or withdraw only the caller's own documents
    (`ISparkCurrentUser.Id`, the id History stamps; `system` in the system context). Refused: duplicate
    slot, invalid slot (`FindInvalidSlot`), `IContributionValidator<T>` errors, re-saving a contribution a
    moderator hid, anonymous. The entity's rows are replaced by what is current after the write (the
    response shows a withdrawn row's previous version).
  - [x] Moderation through a new core contract, `ISatelliteWriteGuard` (Spark.Abstractions): the runtime
    calls every registered guard before writing or withdrawing; Moderation registers
    `ModerationSatelliteWriteGuard` (suspension + lock on an `IModeratable` contribution). No package
    references between the two.
  - [x] Recompute in **before-delete** of a contribution (after SoftDelete; the doomed document counts as
    hidden) and in before-save of a contribution (Restore, direct edits), never after-commit.
  - [x] Pin the current document's change vector (update: cv, create: `""`, delete: `Delete(id, cv)`).
    **Deviation — no in-pipeline retry:** the commit is the base `OnSaveAsync`'s single
    `SaveChangesAsync`, after every hook; nothing can re-read and re-store inside it. A conflict is the
    409 of F7 (`ConcurrencyException` → `SparkConcurrencyException`), atomic (the contribution rolls back
    with the current), and the client's M1c flow is the retry. `RebuildCurrentAsync` and the startup
    rebuild, which own their sessions, retry up to 3 times.
  - [x] Owner delete: a real delete or purge (not SoftDelete's replacement) hard-deletes every
    contribution and current document of the target in the same commit.
- [x] Generator: emit the get-only `[ValueKey] Key` built from the slot tuple, plus its
  `SparkValueObjects.Register` module initializer (F3). **Done in M4.**
- [x] `IContributions.RebuildCurrentAsync(targetId)` (own session, deletes current documents nothing
  backs), plus the startup shape check (`ContributionShapeCheck`, an awaited `IHostedService`: marker
  `SparkContributions/Shapes/{QueryName}`, rebuild of every target streamed from both collections in
  batches of 64, marker written last).
- [x] `AddContributions(params Assembly[])` replaces the placeholder: module initializers of the entry
  assembly and its direct references that reference the Abstractions, of every model entity type's
  assembly (startup and `AppliesTo`), and of the named assemblies. AllFeatures stays unchanged (see M3).
- [x] Contributions references `SoftDelete.Abstractions` with `PrivateAssets="all"` (withdraw/hide),
  so consumers keep SPARK033's meaning.
- Tests: `ContributionsRuntimeTests` (16, real generator via the imported targets) and
  `ModerationSatelliteWriteGuardTests` (1).

### M5b — Runtime surface (server half ✅; client half next, against the contract in the library README)
Server:
- [x] The attribution rendering hint in the model and the batched contributor-name lookup: core's new
  optional `ISparkUserNameResolver` (Spark.Abstractions), registered by `AddHistoryUserNameResolver<T>()`
  too; one batched call per hydrated entity (not in the lazy request — the resolver is app code);
  never stored. The renderer is seeded by synchronize (below), with the History-link options.
- [x] The generated `{Target}{Property}Contributions` query and the model JSON of the generated types:
  new core registry `SparkModelSatellites` (Spark.Abstractions.Model) — satellite model types per
  owner type, with a query to mint once, and renderer seeds per (type, attribute), applied only to an
  attribute without a renderer. `ContributionRegistry.Register` registers both generated types as
  satellites of the target, and the seeds; `ModelSynchronizer` writes their files after the roots,
  `ModelShapeDiscovery` hashes them. The query's source is `Custom.SparkContributionsOfTarget`, served
  by `ContributionActions<T>` (registered as `IPersistentObjectActions<T>` per declaration): a prefix
  load of the parent target's contributions, names filled (`IContribution.ContributorName`, generated
  `[JsonIgnore]`), in memory so Spark composes row security, soft delete, column filters, sort and
  paging.
- [x] `RevertContribution`: an add-on endpoint `POST /spark/po/revert-contribution` (custom actions are
  app-declared in `customActions.json`, so a library verb gets its own route, like History's revert).
  Hides every newer visible contribution of the slot (`reverted`), recomputes, one commit; a hidden
  target is refused ("restore it first"), as is a non-soft-deletable declaration.
- [x] The Q3 notices (withdrawn → whose version is shown, or none is left; someone else's version
  stays), queued and sent after the commit (`OnAfterSaveAsync`).
- [x] `Delete` on the generated current type = remove a whole version: hides every visible
  contribution of the slot (`version-removed`) in the delete's commit.
- [x] Audit of both moderator actions through a new optional core contract `ISatelliteAuditSink`;
  Moderation registers `ModerationSatelliteAuditSink`.
- Tests: `ContributionsSurfaceTests` (13), `ContributionsModelSyncTests` (3, fixed point), the four
  generator snapshots; the `CoSong` fixture now declares all attribution flags.
- Fixed on the way: the .NET client threw on a `notify` operation with `"durationMs": null`
  (`SparkClientOperations`).

Client (ng-spark, new entry point `@mintplayer/ng-spark/contributions`):
- [x] M1c integration: in the conflict merge, contribution rows (the `[Contribution]` property) only
  conflict between two edits by the same user. Another user's change to a slot is theirs-wins with a
  notice, because it is their own contribution document. "Same user" is the server's per-row
  `metadata.contribution.own` (new `PersistentObject.Metadata`, set by the interceptor's
  `OnAfterLoadAsync`), not a client-side id comparison: the row carries no `ContributorId` and
  ng-spark knows no user id, and a boolean keeps raw ids off the target's page.
- [x] The attribution row renderer (`contributionAttribution`, a new generic `rowComponent` kind of
  registration: drawn once per row under the first cell, its attributes dropped from the columns, on
  po-detail and both po-form tables) with the History link; the query-list page reading
  `parentId`/`parentType` and column filters from the URL (generic; grid `presetFilters`, chips); the
  revert action (new generic `SPARK_QUERY_ROW_ACTIONS` grid row-menu extension + a detail action;
  `canRevertContribution` in the permissions endpoint; a success notice); and the generic `lineDiff`
  renderer (LCS, no dependency), compared against the target's row of the same slot.
- [x] Server follow-ups: `ContributorId` seeded `showedOn: PersistentObject`, `isVisible: false` on
  creation (`SparkModelSatellites.SeedNewAttribute`); `lineDiff` seeded on the contribution type's
  string values; SPARK014 and the runtime validator accept rights on the generated type names through
  the CLR fallback (the validator now falls back to the satellite class; SPARK014's lookup ignores
  case like the runtime). Tests: `ContributionsHiddenVisibilityTests`,
  `ContributionsSecurityValidationTests`, `ContributionTypeRightsAnalyzerTests`, new cases in
  `ContributionsSurfaceTests`/`ContributionsModelSyncTests`; ng-spark specs.
- E2E: no demo app has a `[Contribution]` target yet — written with the M6 demo consumer.

### M6 — Demo and tests
- [x] A demo consumer in an existing app (DemoApp, or the QnA sample): a target with a
  `[Contribution]` list of a two-slot element. → QnA `Question.Translations`
  (`QuestionTranslation { [ContributionSlot] Language, [ContributionSlot] Script, Title, Body }`,
  attribution `Contributor | UpdatedAt | History`), the generated contribution type `IModeratable`.
- [x] Integration tests (RavenTestDriver), covering R2–R5 and R8 — the library cases were already
  covered by `ContributionsRuntimeTests`/`ContributionsSurfaceTests`; M6 adds what the real wiring
  needed: `ContributionsAuditedTargetTests` (an `IAuditable` target under History + SoftDelete, a
  per-row protected title) and, against the real QnA host, `QnAContributionsTests` (a member translates
  but cannot retitle, the question is not rewritten, revert/remove-version are moderator-only, a flag
  opens a review case, anonymous cannot write). Covered earlier:
  - one request on load (`NumberOfRequests`)
  - the target is unmodified when only contributions changed
  - owner-only writes, including tampering with the id or `ContributorId`
  - a hide makes the next-latest current, immediately
  - two contributors racing on one slot
  - withdrawing a contribution
  - rebuild
  - a slot value that fails validation
- [x] An E2E test on the demo form: add a version, edit it, remove it
  (`QnAContributionsBrowserTests`: plus the attribution line, the History link with its chips, a
  moderator's revert from the version page, and the withdraw notice). Built; run in the M7 sweep.
- [x] Fixed on the way (found by wiring a real app): History stamped every edit, so a contribution-only
  save rewrote an `IAuditable` target (R3); `--spark-verify-model` refused the generated
  `Custom.SparkContributionsOfTarget` query (no `{Type}Actions` class); a well-known group's `Read` on
  the contribution type failed startup (no row rule) — and a contribution of a hidden target was
  readable by id; the ng-spark edit form ignored the loaded object's per-row read-only attributes.

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

### M8 — Faster test runs (owner decisions, 2026-10-01; CI behaviour unchanged; evidence and rejected alternatives in PRD §5d)
Background:
- Local runs measured 2–2.4× slower than CI per RavenDB-backed suite, and 57% of local test time is
  setup/teardown (per-test databases, an OIDC host booted per test).
- CI runs cost money, so the local sweep must become practical.

Decided (owner, 2026-10-01):
- **(1) Licence:** done by the owner. `RAVENDB_LICENSE` points at the Developer file.
- **(2) One `package.json` script:** affected tests, unit and E2E together.
- **(3) The Nx remote cache stays exactly as it is.**
- **(4) Zero-wait deletion in every `RavenTestDriver` base class,** plus the opt-in
  `SPARK_E2E_SKIP_APP_BUILD`.
- **(5) A database per test class through xUnit class fixtures** (`IClassFixture<SparkSharedDatabase>`,
  xUnit's equivalent of [OneTimeSetUp]/[OneTimeTearDown]). This is the same mode on CI and locally.
- **(6) Keep the RavenTestDriver implementations.** No external `localhost:8080` server for tests.

- [ ] (2) + (4) are being implemented, then one measured run.
- [ ] (5) Migrate classes to `SparkSharedDatabase`, smallest risk first. The investigation counted 181
  driver classes:
  1. the 45 that never write (incl. the OIDC classes: one host per class instead of per test)
  2. the 67 that only need ids scoped via `Id(...)`
  3. the 53 that need unscoped count assertions scoped
  - The 28 with database-wide state stay per-test: subscriptions enumeration, fixed compare-exchange
    keys, revisions config, stop-indexing, background writers.
  - Each migrated class first passes a shuffled-order run (xUnit doesn't guarantee order).
  - CodeCoverage.Tests' `CoverageRavenTest` (458 inline `GetDocumentStore()` calls) follows the same
    pattern.
- [ ] Measure before/after per suite, locally and in the next CI run that happens anyway (never push
  just to measure).

### M7 — Full sweep, docs, PR
- [ ] Write `docs/guide-contributions.md` (API, ids, the current-document cache, rights, moderation
  integration), and add the library to the README.
- [ ] Run all five test projects of the `.slnx` and the ng-spark vitest suites (logs to file).
- [ ] Check the version diff: minor bumps only.
- [ ] Open the PR, then hand off the MintPlayer consumer work (PRD §7).
