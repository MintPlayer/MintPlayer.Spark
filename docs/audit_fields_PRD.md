# PRD — Source-generate the audit and soft-delete members (#271)

**Issue:** [#271](https://github.com/MintPlayer/MintPlayer.Spark/issues/271) — "Source-generate IAudit
boilerplate (CreatedBy/CreatedOn/ModifiedBy/ModifiedOn)", postponed from #210 (N8).
**Plan:** [audit_fields_plan.md](audit_fields_plan.md)
**Status:** drafted 2026-10-05 from a three-agent investigation (inventory, generator conventions,
stamping and model pipeline) on `master` at `ee62764e`. **Owner decisions 2026-10-05:** D1 keep
names, D2 delegated (decided: split `IAuditable` into `IAuditCreated` + `IAuditModified`), D5 overruled
(the generator emits `[Reference(typeof(SparkUser))]` when the library can see `SparkUser`), D8 yes; the remaining decisions follow
the recommendation (§7). **Spikes run and everything implemented 2026-10-05** on `feat/271-audit-members`:
results and deviations from this PRD are in §9, release notes in `docs/release-notes-preview-100.md`.
**One PR.** The generator, the model defaults, the app migrations, and every defect found along the way
(§5) land together.

---

## 1. What the investigation found: half of #271 already shipped

The issue was written before #460 and #482. Those added the runtime half: the interfaces, and code that
stamps them. **#271 is now "generate the member declarations", not "invent IAudit".**

| Contract | Members (CLR type) | Stamped by | Evidence |
|---|---|---|---|
| `IAuditable` (`MintPlayer.Spark.History`, package History.Abstractions, which depends on nothing) | `CreatedBy string?`, `CreatedAt DateTimeOffset?`, `ModifiedBy string?`, `ModifiedAt DateTimeOffset?` | `HistoryInterceptor` (an `IBeforeSave` interceptor in the Finalize stage). On create it stamps all four. On every later write it copies `CreatedBy`/`CreatedAt` back from the stored document, and a save that changes nothing is not stamped. A soft delete stamps `ModifiedBy`/`ModifiedAt`. Sync writes are skipped on purpose. | `libs/history/MintPlayer.Spark.History.Abstractions/IAuditable.cs:21-34`; `libs/history/MintPlayer.Spark.History/HistoryInterceptor.cs:54-111` |
| `ISoftDeletable` (SoftDelete.Abstractions) | `IsDeleted bool`, `DeletedAt DateTimeOffset?`, `DeletedBy string?`, `DeleteReason string?` | `SoftDeleteInterceptor`: a delete becomes a field update; restore clears all four; every other write copies the stored values back | `libs/soft_delete/MintPlayer.Spark.SoftDelete.Abstractions/ISoftDeletable.cs:27-40`; `SoftDeleteInterceptor.cs:47-117` |
| `IModeratable` (Moderation.Abstractions) | `AuthorId string?`, `PostedAt DateTimeOffset?` | `ModerationInterceptor`, on New only, then never changed (means the same as CreatedBy/CreatedAt) | `ModerationInterceptor.cs:57-60` |
| `IContribution` (generated) | `ContributorId string`, `UpdatedAt DateTime` (UTC) | `ContributionHandler.cs:267-298` | `DateTime` chosen on purpose to avoid a `{Name}Raw` index companion |

- **Soft delete is complete, and has an interface** (`ISoftDeletable`). The owner suspected the interface
  was missing; it is not. `DeletedBy`/`DeletedAt` exist, but only as part of the soft-delete
  *behaviour*. There is no audit-only "deleted" interface (§7 D2).
- **The user value is always a user id.** It is the `ClaimTypes.NameIdentifier` claim via
  `ISparkCurrentUser.Id` (`HttpContextSparkCurrentUser.cs:16-28`), never a name or email (#460 D8,
  GDPR). It is `null` for anonymous, system and job writes. Names are resolved when the record is read
  (`IHistoryUserNameResolver`).
- **Time** comes from an injected `TimeProvider?`, falling back to the system clock
  (`HistoryInterceptor.cs:30-32`), so tests can supply a fake clock.
- **Forged `CreatedBy` is already defeated** when `AddHistory()` is registered: a test posts
  `CreatedBy=users/mallory` on both create and update (`tests/MintPlayer.Spark.Tests/History/HistoryTests.cs:130`).
- **The only consumer is QnA**: `Question` and `Answer` declare all 10 members by hand
  (`apps/QnA/QnA.Library/Entities/Question.cs:14,41-75`, `Answer.cs:10,29-60`). `CreatedBy` and
  `ModifiedBy` carry `[Reference(typeof(SparkUser))]`, and the model JSON sets `isReadOnly: true` by hand.

So the issue's three questions are partly answered already:

1. **Packaging:** a lean library generator package exists since #388/#484:
   `libs/source_generators/MintPlayer.Spark.LibraryGenerators` (netstandard2.0, `[ValueObject]` keys
   only). Every `<SparkEntityLibrary>` project already references it. The premise that audit is "the
   only reason for a library-side generator" is out of date.
2. **Versioning and reference flow:** the interfaces already live in dependency-free `.Abstractions`
   packages, and entity libraries already reference those packages.
3. **Who stamps:** #460 decided this — Spark interceptors (§4). What remains are the coverage gaps.

## 2. Goals

- **G1** An entity library declares `public partial class Car : IAuditable` (and/or `ISoftDeletable`,
  `IModeratable`) and writes **none** of the members; a generator emits them.
- **G2** Members the developer *does* declare are respected: the generator emits only what is missing,
  and emits nothing for a fully hand-written entity, so today's QnA compiles unchanged before M6
  converts it. The user-id members (`CreatedBy`, `ModifiedBy`, `DeletedBy`, `AuthorId`) are generated
  with `[Reference(typeof(SparkUser))]`, so nobody has to declare them to get a user reference (D5).
- **G3** Generated audit members land in the model as **read-only** and **shown on the edit page
  only**, without hand-editing the model JSON, and a client cannot write them.
- **G4** Converting QnA to generated members produces only the model diff we expect:
  - `CreatedBy`/`CreatedAt`/`ModifiedBy`/`ModifiedAt`, `IsDeleted`/`DeletedAt`/`DeleteReason` and
    `PostedAt` stay unchanged.
  - `DeletedBy` and `AuthorId` become References, which moves the hash. The owner accepted this: the
    hash changes anyway when someone adds audit fields (D5).
  - SP1 records the actual diff.
- **G5** A misconfiguration fails loudly: a type that is not `partial` is a compile error with a code
  fix (SPARK038); a member with the wrong shape is a compile error.
- **G6** The coverage gaps in §4 are either closed or written down as deliberate, with evidence.

## 3. Non-goals

- **N1** A new stamping mechanism. The interceptors stay (§4, D6).
- **N2** Renaming the shipped members. Recommended in D1; reversible only if the owner decides otherwise.
- **N3** Converting CodeCoverage's `ApiToken` (`CreatedByUserId`, `CreatedAtUtc DateTime`,
  `RevokedAtUtc`, `ApiToken.cs:131-137`). It is production data, those are domain fields (a revoked
  token is not a soft delete), and adopting the interface would mean a data migration plus index changes
  (`VApiToken.cs:46-51`, `ApiTokens_Overview.cs:41`) for no gain. CodeCoverage uses none of History,
  SoftDelete or Moderation. The lifecycle dates on `Build`/`Commit`/`BuildSession` are not audit fields.
- **N4** Stamping writes that bypass Spark: patches, delete-by-query, BulkInsert, ETL. RavenDB raises
  no client-side event for these, so no listener could stamp them.

## 4. Who stamps — the decision that stands, and its gaps

The three options the issue named, compared against the code:

| | (1) RavenDB store/session listener | (2) Spark interceptor (**shipped**) | (3) Entity `Actions` class |
|---|---|---|---|
| Knows the operation (Save/Revert/Restore/Sync) | ❌ could only guess Sync via `SparkSystemContext.IsSystemContext` | ✅ | ✅ |
| Sees the stored document before the edit (needed to keep `CreatedBy` unchanged) | only through `WhatChanged` | ✅ `Before` | ✅ |
| Current user | `IHttpContextAccessor` only, or a hookup per session in `SparkMiddleware.cs:184` that misses library-opened sessions | scoped `ISparkCurrentUser` | scoped |
| Covers raw session writes, migrations, `SaveDocumentUncheckedAsync` | ✅ | ❌ | ❌ |
| Per-entity code | none | none | per entity (Fleet does this today, `CarActions.cs:114-119`) |
| Testable without RavenDB | ❌ | ✅ | ✅ |

**Recommendation (D6): keep (2).** Known gaps, each handled in this PR:

- **Raw writes** (custom actions, jobs, message handlers, migrations, `SaveDocumentUncheckedAsync`,
  `DatabaseAccess.cs:57-91`, `SparkMigrationRunner.cs:63-80`) are not stamped. We document this as
  deliberate. A create-only stamp listener (SP5) is added only if the owner wants it (D7).
- **Replication write-back (suspected bug, SP4).** The non-owner module stamps locally, but
  `SyncActionInterceptor.cs:48-57` forwards only PO attributes marked changed, and the stamp is set on
  the entity, not the PO. `SyncAction` has no initiator field (`SyncAction.cs:21-41`). The owner applies
  the write as Sync and does not stamp. So the editing user is probably lost on both sides, and ETL then
  overwrites the non-owner's stamp with the stale one. If SP4 confirms this, the fix belongs in this PR.
- **History not registered.** Generated members exist, but nothing stamps or protects them. This is new
  exposure that G1 creates (today you would notice when you hand-write the members). It is covered by
  G3 (read-only blocks client writes) and D8 (a diagnostic).
- **Background identity.** There is no service identity, so a job's write is stamped `null`. That is
  accepted and documented. The after-commit payload already carries a `UserId`
  (`SparkInterceptorPipeline.cs:188-196`) as a precedent if this is ever wanted.

## 5. Design

### 5.1 Generator

- **Where:** a new `AuditMembersGenerator` (with reporter, `.Rules.cs` and a partial-class code fix) in
  **`MintPlayer.Spark.LibraryGenerators`**. Reasons:
  - Entity libraries and AllFeatures already reference and pack this project.
  - Adding it there needs no new project, no new `MintPlayer.SourceGenerators.Tools` version pin (memory:
    split generator versions break Visual Studio), and no new row in `AnalyzerPackagingTests`.
  - Alternative (D3): embed the generator in each `.Abstractions` package, the way Contributions does
    (C7). Versions would always match, but that adds a fifth generator project and puts an analyzer into
    packages that "depend on nothing".
- **Trigger: the interface, not an attribute.** The interface is already the runtime opt-in, so an
  attribute would be a second source of truth. An interface trigger also fails loudly: if the generator
  reference is missing you get CS0535. An attribute trigger fails silently, which is what happens with a
  `[ValueObject]` today (memory: `reference_valueobject_needs_generator_reference`).
  - Use `CreateSyntaxProvider` over `TypeDeclarationSyntax { BaseList: not null }`, then check that the
    type's **direct** `Interfaces` include a known interface.
  - Match by fully-qualified display name, not `GetTypeByMetadataName`, which returns null when two
    assemblies define the name (memory: `entity_library_split_PRD.md:292-294`).
  - If the interface's assembly is not referenced, emit nothing.
- **What it emits:** a table maps each interface's metadata name to its member list (name, type,
  doc comment) for `IAuditCreated`, `IAuditModified` (and so `IAuditable`), `ISoftDeletable` and `IModeratable` (D4, D2). This is the first generator
  in the repo that fills in an interface's members.
- **Rules:**
  1. **Emit only the missing members.** Look a member up by name on the type, its base types and any
     explicit implementations. A fully declared type gets no output and no diagnostic.
  2. **The user-id members get `[Reference(typeof(SparkUser))]` when the compilation can see
     `SparkUser`** (D5, owner 2026-10-05). These are `CreatedBy`, `ModifiedBy`, `DeletedBy` and
     `AuthorId`; each one stores a `SparkUser` id (#460 D8). The generated entity is:
     ```csharp
     public partial class Question : IAuditable { }   // the developer writes this
     // the generator emits:
     [global::MintPlayer.Spark.Abstractions.Reference(typeof(global::MintPlayer.Spark.Authorization.Identity.SparkUser))]
     [global::System.ComponentModel.ReadOnly(true)]
     public string? CreatedBy { get; set; }
     ```
     - **The type stays `string?`.** The interface member is nullable, and system, anonymous and job
       writes store `null`.
     - **Conditional (owner 2026-10-05, §5.3b).**
       - The attribute is emitted only when both
         `MintPlayer.Spark.Authorization.Identity.SparkUser` and
         `MintPlayer.Spark.Abstractions.ReferenceAttribute` resolve in the compilation.
       - Otherwise the member is a plain `string?`, and the generator reports **SPARK040 (Info)** on the
         type: "`CreatedBy` was generated without `[Reference]`; reference
         `MintPlayer.Spark.Authorization.Abstractions` to link it to `SparkUser`."
       - Adding that reference later turns the field into a Reference and moves the model hash. The
         owner accepts this, because adding audit fields moves the hash anyway.
     - **No partial-property support.** A developer who wants something different declares the whole
       member, and rule 1 skips it.
  3. **If a base class already implements the interface, emit nothing.** Re-emitting would give CS0108.
  4. **`partial` is required only when something is missing** — on the type and on every containing
     type. Otherwise report **SPARK038 (Error)** with a code fix, mirroring SPARK016. SPARK038 is the
     next free id (`docs/diagnostics.md`).
  5. **Records, generic types and nested types** get the correct header. Reuse Contributions'
     `PartialHeader` / `IsPartialAllTheWayUp` (`ContributionInspector.cs:380-406`), not the ValueObject
     producer's hard-coded `partial class` (§5.4).
  6. **A declared member with the wrong type** already gives CS0738. No new diagnostic.
  7. **Each emitted member gets a `///` summary.** Generators cannot see each other's output, so the
     #348 description generator never sees the generated members. The model description therefore comes
     from a description in the generator's own table, emitted the same way #348 reads it, or is accepted
     as absent (SP1 measures which).
- **Never `[IgnoreProperty]`:** History, grids and row filters (`CarActions.cs:48`) read these members.

### 5.2 Model defaults (G3)

- **Today:** synchronize sets `IsReadOnly = !CanWrite`, only when it creates the attribute
  (`ModelSynchronizer.cs:930-934`). A generated `{ get; set; }` member would therefore arrive writable and
  be shown in both grid and form.
- **The protection when the attribute is read-only:** the mapper refuses read-only attributes
  (`EntityMapper.cs:590-597`, `SaveValidation.cs:79-88`).
- **The problem when it is not:** a forged value reaches `MapAsync` and the Default-stage interceptors
  before History overwrites it in the Finalize stage. Without `AddHistory()`, nothing overwrites it at
  all.
- **Proposal (D9):** the generator puts the BCL `[System.ComponentModel.ReadOnly(true)]` on the members
  it emits. Today nothing in `libs/` uses it, and it adds no dependency.
  - The synchronizer honours it when it creates an attribute: `IsReadOnly = true`, and `ShowedOn`
    `PersistentObject` for the `*By`/`*At` members (`DeletedBy`/`DeleteReason` follow QnA's current
    `showedOn`).
  - `IsReadOnly` is not part of the model hash (`SparkModelShape.cs:69-90`), so G4 is unaffected.
  - Synchronize never changes an existing attribute, so a deliberate owner override in the JSON survives
    (#253).
- **Also:** an `IAuditableStartupCheck`, alongside the existing `SoftDeleteStartupCheck.cs`. It catches
  hand-written members with the wrong accessibility, which the generator does not prevent.

### 5.3 Naming and types (D1)

**Keep the shipped names and types: `CreatedBy`/`CreatedAt`/`ModifiedBy`/`ModifiedAt`, all nullable,
timestamps `DateTimeOffset?`.**

- **The repo's convention is `…At`.** Every audit timestamp ends in `At` (`CreatedAt`, `ModifiedAt`,
  `DeletedAt`, `PostedAt`, `UpdatedAt`). The only `On` is `SparkMigrationRecord.AppliedOnUtc`.
- **CronosCore's `CreatedOn`/`ModifiedOn` would mean renaming:**
  - stored QnA fields (a data migration);
  - `SparkHistory.AuditFields` (`SparkHistory.cs:144-147`);
  - the hard-coded `'ModifiedAt'`/`'ModifiedBy'` in ng-spark `spark-po-edit.component.ts:549-581`.
- **Non-null `CreatedBy`, as CronosCore has it, does not fit.** System and anonymous writes have no user
  id, and RavenDB has no `@created` timestamp to backfill from, only the oldest revision.
- **`DateTimeOffset` costs little for these members.** Every stamp is `GetUtcNow()` (+00:00), so the
  offset lost at index time (memory: DTO projection) loses nothing. Each one does need a `{Name}Raw`
  companion when sorted in a hand-written index (`guide-dates-and-sorting.md`). Changing the type to
  `DateTime` would break a shipped API, so we accept that cost.
### 5.3a Lean interfaces (D2, decided 2026-10-05)

The owner leaned towards "split it in lean interfaces" and left the decision to us. Decision: **split
`IAuditable`, but do not add a "deleted" interface.**

```csharp
namespace MintPlayer.Spark.History;               // History.Abstractions
public interface IAuditCreated  { string? CreatedBy { get; set; }  DateTimeOffset? CreatedAt { get; set; } }
public interface IAuditModified { string? ModifiedBy { get; set; } DateTimeOffset? ModifiedAt { get; set; } }
public interface IAuditable : IAuditCreated, IAuditModified { }
```

- **Why split.** There is a real consumer for the halves. Fleet `Car` stores only `CreatedBy`
  (`Car.cs:106-108`), so `Car : IAuditCreated` replaces its hand-written stamping without adding
  modification fields Fleet never asked for. An immutable record (a log entry, an import) also wants
  only the created pair. The names match CronosCore's `IAuditCreated`/`IAuditModified`.
- **Why no `IAuditDeleted`.**
  - A `DeletedBy`/`DeletedAt` pair means nothing without soft delete: a hard delete removes the
    document.
  - The pair would have to live in History.Abstractions, so SoftDelete.Abstractions would depend on
    History.Abstractions only to share two property names.
  - `ISoftDeletable` stays the single opt-in for both the behaviour and its four fields.
- **`IModeratable`** keeps `AuthorId`/`PostedAt` and does not extend `IAuditCreated`. They are the
  moderation-domain author. `QuestionTranslationsContribution` implements `IModeratable` without being
  audited, so merging the two would force audit semantics onto it.
- **Stamping follows the halves.** `HistoryInterceptor` stamps the created pair when the entity is
  `IAuditCreated`, and the modified pair when it is `IAuditModified`. It currently checks `IAuditable`
  at `HistoryInterceptor.cs:41,73,77,81,104`. The revision attribution and diff exclusions in
  `SparkHistory` (`:49,53,65,146`) key on `IAuditModified` and on the member names.
- **Source compatibility.** `IAuditable` keeps exactly the same four members, now inherited, so
  implicit implementers (QnA, `CaPost`, `HiNote`) compile unchanged. An *explicit* implementation
  (`string? IAuditable.CreatedBy`) would break; none exists in this repo. This is a preview minor bump,
  and the release notes say so (D11).

### 5.3b Package dependencies: none added (D5 refined 2026-10-05)

An earlier draft gave the three `.Abstractions` packages dependencies on Attributes and
Authorization.Abstractions, so that the generated `[Reference]` would always compile. The owner proposed
instead to check whether the target compilation can see `SparkUser`, and that design replaces the
earlier one.

- **A generator reference contributes no compile references.** `MintPlayer.Spark.LibraryGenerators` is
  `DevelopmentDependency=true` / `IncludeBuildOutput=false`, its own package references are all
  `PrivateAssets="all"` (`MintPlayer.Spark.LibraryGenerators.csproj:7-8,26-28`), and in-repo it is wired
  `ReferenceOutputAssembly="false"` (`QnA.Library.csproj:32`). Referencing it therefore never brings in
  `SparkUser`. Whether `SparkUser` is visible depends only on the library's own references, which is
  the right thing for the generator to key on.
- **The `.Abstractions` packages stay dependency-free.** The "depends on nothing" descriptions stand.
- **Detection:**
  - Resolve both types through `Compilation.GetTypeByMetadataName`. A `null` result, whether the type is
    missing or ambiguous, emits no attribute.
  - It is a compilation-level fact, so it is computed once per compilation in the incremental pipeline
    (combined with the per-type models), not per type.

### 5.4 Defects found along the way (fixed in this PR)

- **F1:** `ValueObjectKeyProducer.cs:54` hard-codes `partial class {Name}`. Reading the code, a
  `[ValueObject] record` fails with CS0261, and a generic value object loses its type parameters. No test
  covers either. The fix shares the header helper from §5.1 rule 5.
- **F2 (if SP4 confirms):** replication write-back loses the editing user (§4).
- **F3 (if SP6 confirms):** the AllFeatures package packs `SourceGenerators.dll` and
  `LibraryGenerators.dll` without `MintPlayer.SourceGenerators.Tools.dll`.
  `Contributions.SourceGenerators.csproj:23-29` warns that this shape throws FileNotFoundException in a
  consumer's compiler. It has not been measured.

### 5.5 App migrations

| App | Change | Constraint |
|---|---|---|
| QnA `Question`, `Answer` | Delete **all** hand-written audit, soft-delete and moderation members; the types become `partial`. ~~The `QuestionTranslationsContribution` `IModeratable` members become generated too.~~ Superseded (§9): they are explicit read-only views over the contribution, so the generator rightly leaves them. | Only the expected diff (G4, SP1) |
| Fleet `Car` | Replace the `[IgnoreProperty] string? CreatedBy` (`Car.cs:106-108`) and its stamping in `CarActions.cs:114-119` with **`IAuditCreated`** plus `AddHistory()`. The row filter at `CarActions.cs:48` keeps reading `CreatedBy`. | Demo data; the model JSON gains 2 read-only attributes (D10) |
| CodeCoverage | none (N3) | production |
| DemoApp, HR | none (no audit fields) | — |

## 6. Spikes

Each spike gets a written verdict in §9 before the milestone that depends on it starts.

| # | Question | How | Decides |
|---|---|---|---|
| **SP1** | What exactly does converting QnA to generated members change in the model JSON and hash? Is it only `DeletedBy`/`AuthorId` becoming References? | Convert `Question` on a scratch branch, run `--spark-synchronize-model`, diff `modelHashes.json` and `Question.json` | G4, whether a description goes in the table (rule 7) |
| **SP2** | Is a generated `[Reference(typeof(SparkUser))]` safe? (a) Does breadcrumb resolution of the reference respect read rights on `SparkUser`, or does it leak display names to someone who can read the row but not the user (memory: breadcrumbs raw, fixed in M2c)? (b) What does the UI show for an id with no `SparkUser` document (external-IdP-only app, deleted user)? (c) Does the packed `History.Abstractions` nuspec now pull only the two plain-SDK packages? | A SparkTestDriver test reading a `Question` as a visitor and as a non-admin; `dotnet pack` + nuspec inspection | D5 safe default; whether the generated reference needs `showedOn`/rights defaults |
| **SP3** | On a **new** generated entity, does `[ReadOnly(true)]` make synchronize write `isReadOnly: true`? Without History, is a forged `CreatedBy` POST refused? Does a Default-stage interceptor see a forged value? | A SparkTestDriver test with a JSON fixture; post forged values with and without `AddHistory()` | D9, G3 |
| **SP4** | Replication write-back: after a non-owner module edits a replicated `IAuditable` entity, what are the owner's and the non-owner's `ModifiedBy`/`ModifiedAt`? | Two-module test (existing cross-module sync test fixture) | F2 |
| **SP5** | **Optional, only if D7 = yes.** Does a change made in `OnBeforeStore` persist on the RavenDB 7.x client? Which paths fire it (migration, job, `SaveDocumentUnchecked`)? What user does `IHttpContextAccessor` give? Does it double-stamp pipeline saves or Sync echoes? | A throwaway listener in a test | D7 |
| **SP6** | Does a consumer of the **packed** AllFeatures nupkg load the library generator, or does the missing Tools.dll throw? | `dotnet pack` to a local feed; a scratch console/library project referencing it; build log | F3 |

## 7. Decisions

| # | Question | Decision | Source / why |
|---|---|---|---|
| **D1** | Names: shipped `CreatedAt`/`ModifiedAt`/`DeletedAt`, or CronosCore's `CreatedOn`/`ModifiedOn`? | **Keep `…At`** | **Owner 2026-10-05.** §5.3 |
| **D2** | Lean interfaces? | **Split `IAuditable` into `IAuditCreated` + `IAuditModified`; no `IAuditDeleted`; `IModeratable` stays separate** | **Delegated by the owner 2026-10-05** ("I was gonna say split it in lean interfaces, but I'll let you decide"). §5.3a: Fleet is a real created-only consumer; a deleted pair has no meaning without soft delete |
| **D3** | Generator home | **`LibraryGenerators`** | Recommendation, not contested. §5.1 |
| **D4** | Generate `IModeratable` and the soft-delete members too? | **Yes, all three contracts** (four interfaces) | Recommendation, not contested. The Contributions generator already emits the `ISoftDeletable` members (`ContributionsEmitter.cs:71-80`) |
| **D5** | How does `[Reference(typeof(SparkUser))]` get there? | **The generator emits it on the user-id members whenever `SparkUser` is resolvable; otherwise a plain `string?` plus SPARK040 (Info)** | **Owner 2026-10-05:** "if the dev decides to add audit fields, the modelhash will change anyway. So you may as well generate `[Reference(...)] public string CreatedBy {g;s}`". It overrules the recommendation (a). It has two consequences: §5.3b package dependencies, and the type stays `string?` (rule 2) |
| **D6** | Who stamps | **Keep the interceptors** | Recommendation, not contested. §4 |
| **D7** | A store-level create-only stamp for raw writes? | **No**, the gap is documented | Recommendation, not contested. SP5 is not run |
| **D8** | Warn when an app uses the interfaces without the runtime package | **Yes: SPARK039 (Warning)** | **Owner 2026-10-05** |
| **D9** | Read-only marker | **BCL `[ReadOnly(true)]`** | Recommendation, not contested |
| **D10** | Convert Fleet | **Yes, as `IAuditCreated`** | Recommendation + D2 |
| **D11** | Versioning | Next NuGet `11.0.0-preview.N`, **not a major**. npm only if ng-spark changes | CLAUDE.md versioning rule |
| **D12** | PR shape | **One PR** for everything, F1–F3 included | **Owner 2026-10-05:** "Single pull-request - don't shard related fixes across several pull-requests" |

## 8. Acceptance

- QnA compiles with **every** audit, soft-delete and moderation member removed. The model diff is
  exactly the one SP1 recorded and G4 accepts.
- A new entity library type `public partial class X : IAuditable {}` gets read-only, edit-page-only
  attributes after synchronize. `CreatedBy`/`ModifiedBy` are References to `SparkUser`. A forged
  `CreatedBy` POST is refused, with or without History.
- `public partial class Y : IAuditCreated {}` gets only the created pair, and History stamps only that
  pair.
- Generator tests cover:
  - all members missing;
  - partly declared;
  - `IAuditCreated` only;
  - `IAuditModified` only;
  - `IAuditable`;
  - `[Reference(typeof(SparkUser))]` emitted on all four user-id members when Authorization.Abstractions
    is referenced; a plain `string?` plus SPARK040 when it is not;
  - a base that implements the interface;
  - a type that is not partial (SPARK038 + code fix);
  - a record;
  - a nested type;
  - a generic type;
  - History not referenced;
  - the interface coming from a metadata reference;
  - the F1 value-object record/generic case.
- `docs/diagnostics.md` has rows for SPARK038, SPARK039 and SPARK040. A guide `docs/guide-auditing.md` covers the
  interfaces, the generator, who stamps, and the gaps from §4.
- Full local sweep (`npm run test:affected`) is green.

## 9. Spike results

All run 2026-10-05.

| # | Verdict | Evidence |
|---|---|---|
| **SP1** | ✅ The model diff from converting QnA is exactly the expected one: `AuthorId` and `DeletedBy` on `Question` and `Answer` become `Reference` → `SparkUser`. Nothing else in the model moved, including order, `isReadOnly`, `showedOn`, the `qna-author` renderer and `translations.json`. Generated `///` summaries do not reach `translations.json`, because existing descriptions are kept; rule 7 needs nothing more. | `--spark-synchronize-model` on QnA; the diff is in the QnA commit of this branch (`apps/QnA/QnA/App_Data/Model/{Question,Answer}.json`, `modelHashes.json`) |
| **SP2** | ✅ Safe by an existing decision, with no code change. A reference label is resolved through **row security on the target** (`BreadcrumbResolver.cs:210`), plus attribute rights on the target (`:228`), not through a type right. So a `SparkUser` reference renders the user's `{UserName}` to anyone who can read the row. #264 made the user name a public handle (G-Q15/G-Q22), and QnA already ships exactly this, covered by `tests/MintPlayer.Spark.E2E.Tests/QnA/QnAAuditFieldsTests.cs`. A dangling id (deleted user) is skipped by the loader (`BreadcrumbResolver.cs:198`), so no label renders. The guide warns that an app with a more private `SparkUser` breadcrumb must fix the breadcrumb. | code reading + the existing E2E test |
| **SP3** | ✅ A new `[ReadOnly(true)]` member is created `isReadOnly: true`, `isRequired: false`, `showedOn: PersistentObject`. Fleet `Car.CreatedBy`/`CreatedAt` came out exactly so. A forged value is dropped by `EntityMapper.IsWritableBySchema` (`EntityMapper.cs:590-597`), silently, before any interceptor runs, with or without History. | Fleet sync diff; `ModelSynchronizerTests.A_ReadOnly_member_…`; `MassAssignmentTests.PUT_does_not_modify_isreadonly_attribute` rewritten to forge the real attribute |
| **SP4** | ✅ **F2 confirmed** by reading the code: the non-owner forwards only `IsValueChanged` PO attributes (`SyncActionInterceptor.cs:49-52`), the owner saves as `Sync` (`SyncActionHandler.cs`), and `HistoryInterceptor` returned early on `Sync`. **Fixed** in this PR: `SyncAction.InitiatorId`, `ISparkSyncInitiator`, and History stamping `Sync`. A two-module E2E was not built: the three hops are each covered by a unit test (`SyncActionInterceptorTests`, `SyncApplyEndpointTests`, `AuditStampingTests`). | code reading; tests named |
| **SP5** | ⏹ Not run (D7 = no). | — |
| **SP6** | ✅ **F3 not confirmed — no change.** The packed AllFeatures nupkg has no `MintPlayer.SourceGenerators.Tools.dll`, but a scratch consumer built from the local feed **does** run `LibraryGenerators` (it emitted `SparkValueObjectKeys.g.cs`). `Tools.dll` arrives transitively in `analyzers/dotnet/roslyn5.9/cs` of `MintPlayer.AspNetCore.Endpoints` 11.3.0-rc.0, which is fragile but working. Packing a second copy would risk the split-version problem (memory: split generator versions), so it was left alone. | `dotnet pack` + `dotnet build -p:EmitCompilerGeneratedFiles=true` of a scratch consumer; `obj/project.assets.json` |

### Deviations from §5 found while implementing

- **The header helper is local to LibraryGenerators** (`Generators/PartialTypeHeader.cs`), not linked
  from Contributions. Contributions keeps its own copy, and the two share no project.
- **A generic `[ValueObject]` is not fixed.** F1 fixes records. A generic value object also fails in the
  *registry* (`typeof(Slot<TValue>)` in a non-generic class), which was never supported. That is not a
  regression, and no app has one.
- **Moderation does not stamp a `Sync` insert.** `ModerationInterceptor` runs no `Sync` write at all,
  by design ("the owner module already decided"), and no replicated `IModeratable` exists. Its
  `AuthorId` is therefore left as the replica sent it. History's `Sync` stamping does not depend on this.
- **The SPARK039 fixture is the application only.** It scans the application and the non-framework
  assemblies it references for implementers. A referenced runtime assembly counts as registered: whether
  `Add…()` is called is not visible to an analyzer.
- **`HistoryInterceptor` no-change check now covers `Sync` too**, so a write-back that changes nothing
  is not stamped, the same as a `Save`.
