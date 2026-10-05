# Plan — Source-generate the audit and soft-delete members (#271)

**PRD:** [audit_fields_PRD.md](audit_fields_PRD.md)
**Branch:** `feat/271-audit-members` off `master` (`ee62764e`) — not created yet
**One PR.** The generator, the model defaults, the QnA/Fleet conversions, F1–F3 and the docs land
together. Tests run once, in the final sweep (M8). Earlier milestones are checked by building and
reading the code.

## Status

| Milestone | State |
|---|---|
| Investigation (3 agents) | ✅ 2026-10-05 → PRD §1, §4 |
| Owner decisions | ✅ 2026-10-05 → PRD §7 (D1 keep names, D2 split, D5 `[Reference]` when SparkUser resolves, D8 yes, D12 one PR) |
| M0 spikes SP1–SP4, SP6 (SP5 dropped, D7 = no) | ✅ PRD §9: F2 confirmed, F3 not |
| M0b lean interfaces `IAuditCreated`/`IAuditModified` + History stamps by half (no package deps, §5.3b) | ✅ with M5 F2 in one commit |
| M1 generator + SPARK038 + SPARK040 + code fix | ✅ |
| M2 F1 ValueObject header | ✅ records (generic value objects: PRD §9 deviation) |
| M3 model defaults (`[ReadOnly]` in synchronize) + `IAuditable` startup check | ✅ |
| M4 SPARK039 (runtime package missing) | ✅ |
| M5 F2 write-back initiator / F3 packaging (only if confirmed) | ✅ F2 fixed; F3 not confirmed, no change |
| M6 QnA + Fleet conversions | ✅ SP1 diff as expected |
| M7 docs, versions (preview.100), release notes | ✅ `guide-auditing.md`, `release-notes-preview-100.md` |
| M8 full sweep | ⏳ |

## M0 — Spikes

Run on a scratch branch and throw the code away. Write each verdict, with its evidence, into PRD §9.

- **SP2 first** (safety of the generated `[Reference(typeof(SparkUser))]`):
  - Read a QnA `Question` as a visitor and as a non-admin user through a SparkTestDriver test. Check
    whether breadcrumb resolution of `CreatedBy` returns a `SparkUser` display name to someone who has
    no read right on `SparkUser`.
  - Check what an id without a `SparkUser` document renders as.
  - If names leak: the generated attribute or its model default needs a rights or `showedOn` answer,
    designed in PRD §9 before M1.
- **SP1.** Convert QnA `Question` by hand into the exact shape the generator will emit (a partial file
  with the members, `[Reference]` on the four user-id members, `[ReadOnly(true)]`, `///` summaries).
  Run
  `dotnet run --project apps/QnA/QnA -- --spark-synchronize-model`, then
  `git diff apps/QnA/QnA/App_Data/Model/` and the model hashes. Record any diff in attribute order or
  description.
- **SP3.** A temporary SparkTestDriver test with a JSON fixture. Entity `X : IAuditable` whose members
  carry `[ReadOnly(true)]`. Assert what synchronize writes, then post a forged `CreatedBy` with and
  without `AddHistory()`.
- **SP4.** Use the existing cross-module sync test infrastructure (see `docs/guide-cross-module-sync.md`).
  A non-owner module edits a replicated `IAuditable` entity. Read `ModifiedBy`/`ModifiedAt` on both
  modules after ETL.
- **SP6.** `dotnet pack` AllFeatures to a local feed under the scratchpad. Reference it from a scratch
  library containing a `[ValueObject]`, and build with the log redirected to a file. A
  FileNotFoundException for Tools.dll confirms F3.

## M0b — Lean interfaces (PRD §5.3a, §5.3b)

- **History.Abstractions:**
  - add `IAuditCreated` (`CreatedBy`, `CreatedAt`) and `IAuditModified` (`ModifiedBy`, `ModifiedAt`);
  - make `IAuditable : IAuditCreated, IAuditModified` with no members of its own;
  - carry the existing doc comments over (`IAuditable.cs:21-34`).
- **`HistoryInterceptor`** (`:41,73,77,81,104`) stamps the created pair when the entity is
  `IAuditCreated`, and the modified pair when it is `IAuditModified`. `CreatedBy`/`CreatedAt` are
  still copied back from the stored document.
- **`SparkHistory`** (`:49,53,65`) attributes revisions through `IAuditModified`. `:146` keeps the
  member names.
- **Package references:** none added. The `.Abstractions` packages stay dependency-free (PRD §5.3b).
- **Tests (run in M8):** extend `HistoryTests.cs` with an `IAuditCreated`-only entity. Assert that the
  created pair is stamped and that no modified fields exist or are stamped.

## M1 — `AuditMembersGenerator` in `MintPlayer.Spark.LibraryGenerators`

- **Generator:** `Generators/AuditMembersGenerator.cs`, with a Producer and an `IDiagnosticReporter`,
  in the same style as `ValueObjectKeyGenerator.cs:31-65`.
  - Use `CreateSyntaxProvider` over `TypeDeclarationSyntax { BaseList: not null }`.
  - Semantic step: take the type's `AllInterfaces`, minus those its base type already implements, and
    compare them by display string against a table (`AuditContracts.cs`):
    `MintPlayer.Spark.History.IAuditCreated`, `MintPlayer.Spark.History.IAuditModified`,
    `MintPlayer.Spark.SoftDelete.ISoftDeletable`, `MintPlayer.Spark.Moderation.IModeratable`.
    - `IAuditable` expands through its two bases, so it needs no table row.
    - Copy the exact namespaces from the source files.
  - The model is equatable (ValueComparer), so the incremental cache works.
- **Members:** for each one, decide *absent* (emit the full property) or *present / inherited* (emit
  nothing).
  - Every emitted member gets `[global::System.ComponentModel.ReadOnly(true)]` (D9) and a `///` summary.
  - The user-id members (`CreatedBy`, `ModifiedBy`, `DeletedBy`, `AuthorId`) also get
    `[global::MintPlayer.Spark.Abstractions.Reference(typeof(global::MintPlayer.Spark.Authorization.Identity.SparkUser))]`
    (D5), but **only when** both types resolve through `Compilation.GetTypeByMetadataName`.
    - Compute this once per compilation from `context.CompilationProvider`, and `Combine` it with the
      per-type models. The result is a `bool`, so it caches well.
    - When the types don't resolve, emit a plain `string?` and report **SPARK040 (Info)** on the type.
    - Add a SPARK040 row to `docs/diagnostics.md`.
- **Headers:** take the header helper from Contributions (`ContributionInspector.cs:380-406`) and move it
  to a file shared by both generators via `Compile Include` links, like AllFeatures does.
- **SPARK038 (Error):** a type that implements a contract, is missing members, and is not partial all
  the way up.
  - `AuditMembersReporter.Rules.cs`
  - `AuditPartialCodeFixProvider.cs`, modelled on `ValueObjectPartialCodeFixProvider.cs`
- **Tests:** `tests/MintPlayer.Spark.SourceGenerators.Tests/Generators/AuditMembersGeneratorTests.cs`,
  modelled on `ValueObjectKeyGeneratorTests.cs` (`generatorAssemblyName: "MintPlayer.Spark.LibraryGenerators"`,
  `ShouldHaveNoErrors` on generator plus compiler errors). Cases are in PRD §8. Add a code-fix test in
  `Diagnostics/`.
- **Contributions (D4):** decide whether `ContributionsEmitter.cs:71-80` should stop emitting the
  `ISoftDeletable` members and leave them to this generator.
  - ⚠️ Contributions' generated type sits in the app *or* the library, and the library generator is not
    referenced everywhere.
  - Default: keep Contributions' own emission. Make the new generator skip types that are
    `[Contribution]`-generated, by checking the already-implemented members (rule 1 does this anyway).

## M2 — F1 ValueObject header

- Replace `ValueObjectKeyProducer.cs:54` with the shared header helper.
- Add `[ValueObject] record` and generic value-object cases to `ValueObjectKeyGeneratorTests.cs`. Watch
  them fail before the fix and pass after.

## M3 — Model defaults

- **Synchronizer:** in `ModelSynchronizer.cs` at the point where an attribute is created (`:922-950`),
  read `System.ComponentModel.ReadOnlyAttribute` on the CLR property.
  - `IsReadOnly = !CanWrite || readOnlyAttr?.IsReadOnly == true`.
  - Set `ShowedOn = PersistentObject` for the members marked read-only. Check that this matches QnA's
    current JSON exactly (SP1).
  - The existing-attribute path stays untouched (#253 never-delete / owner override).
- **Check the other reflection paths:** the six paths from #253/#254 that reflect over properties
  directly. `EntityMapper.IsWritableBySchema` already keys on the model, so it needs no change. Verify
  this by reading the code.
- **Startup check:** `IAuditableStartupCheck` in `libs/history/MintPlayer.Spark.History`, mirroring
  `SoftDeleteStartupCheck.cs:17-92`. The members must be public and read/write.

## M4 — SPARK039 (Warning)

- **Where:** in `MintPlayer.Spark.SourceGenerators`, the app-side generator.
- **When:** a referenced or compiled type implements `IAuditable` / `ISoftDeletable` / `IModeratable`,
  and the app compilation references no `MintPlayer.Spark.History` / `.SoftDelete` / `.Moderation`
  assembly respectively. The message names the missing package and its `Add…()` call.
- **Library types:** they come from metadata, so there is no source location. Report on the app's
  assembly (memory: analyzers can't locate types in another assembly).
- **Tests:** add them to the generator test project.

## M5 — Confirmed defects only

- **F2** (SP4 confirmed). Put the initiator into the write-back path:
  - add the initiator to `SyncAction` (`SyncAction.cs:21-41`);
  - on the owner, apply a Sync write of an `IAuditable` entity with `ModifiedBy`/`ModifiedAt` taken from
    the action instead of skipping the stamp.
  - Design the exact shape after SP4, and document it in PRD §9 first.
- **F3** (SP6 confirmed). Add `MintPlayer.SourceGenerators.Tools.dll` to AllFeatures'
  `analyzers/dotnet/roslyn5.9/cs` (`AllFeatures.csproj:58-65`). Extend `AnalyzerPackagingTests.cs:39-58`
  to assert it.

## M6 — App conversions

- **QnA:**
  - `Question.cs` and `Answer.cs`: delete **all** hand-written audit, soft-delete and moderation
    members, and mark the types `partial`.
  - `QuestionTranslation.cs:30`: the `IModeratable` members become generated.
  - Run synchronize. Expect exactly the diff SP1 recorded (`DeletedBy`/`AuthorId` become References).
    Any other diff must be explained in PRD §9 before it is accepted.
- **Fleet:**
  - `Car : IAuditCreated` (D2, D10); delete `Car.cs:106-108` and `CarActions.cs:114-119`.
  - Add `AddHistory()` to Fleet's `Program.cs`. Check what History brings with it: revisions config, and
    whether the Developer licence is required for revisions (memory: licence tiers).
  - The row filter `CarActions.cs:48` keeps reading `CreatedBy`.
  - Synchronize, then review the model JSON diff (2 new read-only attributes: `CreatedBy` as a Reference, `CreatedAt`).
  - Check Fleet's E2E fixtures and tests for `CreatedBy` (Fleet is an E2E host app).

## M7 — Docs and versions

- **`docs/guide-auditing.md`:**
  - the interfaces;
  - "declare the interface, the generator does the rest";
  - partial `[Reference]` declarations;
  - who stamps and what is *not* stamped (PRD §4);
  - why `ISparkCurrentUser` stores ids only;
  - `[ReadOnly(true)]`.
- **`docs/diagnostics.md`:** rows for SPARK038, SPARK039 and SPARK040.
- **Release notes:** the next `docs/release-notes-preview-N.md`.
- **Versions:** bump the NuGet preview number of the touched packages (LibraryGenerators,
  SourceGenerators, History, Spark core, AllFeatures; SoftDelete/Moderation only if touched). **No major
  bump** (D11). Check the version diff.
- **Project memory:** update it with the result (a `project_issue271_audit_members.md`).

## M8 — Sweep

Run with the output written straight to a file in the scratchpad, then grep the file:

```
RAVENDB_LICENSE='C:\Repos\MintPlayer.Spark\.secrets\raven-license.log' npm run test:affected > <scratchpad>/sweep.log 2>&1; echo "EXIT: $?"
```

- Run it in the background and wait for the notification.
- After it passes, also build the solution clean, and prove the generator test is not vacuous: comment
  out one generated member in the table, watch QnA fail with CS0535, then restore it.
