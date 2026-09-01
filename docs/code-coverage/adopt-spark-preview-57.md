# Adopt Spark 10.0.0-preview.57 — showedOn fix, complex-field indexing, breadcrumb rework, synchronizer preservation, query-declared index bindings

**Status: ✅ SHIPPED 2026-08-20 (M1–M5) · [PR #15](https://github.com/MintPlayer/CodeCoverage/pull/15) squash-merged to master as `180879b` · upgrade preview.53 → preview.57 (four releases)**

As-built notes, all conscious:

- **The "verify passes before synchronize" claim was too broad — and the exception is instructive.**
  `--spark-verify-model` on the bumped-but-unsynchronized tree exits **3**, not 0. The four hashes that
  moved are the **entity (CLR-shape)** hashes — `Account`, `Build`, `Commit`, `Repository` — not the file
  hashes. Cause: removing the four class-level `[Breadcrumb]` attributes (mandatory under `.55`) genuinely
  changes the CLR shape that `SparkModelShape` describes. The no-flag-day property is real but belongs to
  `.56`'s query stamping alone (un-stamped queries contribute no hash lines); **any release in the hop that
  changes entity CLR shape moves the entity hashes regardless.** A consumer on `.55` upgrading only to
  `.56` would indeed see verify pass first. Corrected in the body below.
- Everything else landed exactly as predicted: seven `SPARK_INDEX_010` warnings (six VBuild fields —
  `FlagCoverage` among them — plus `VRepository.LatestCoverage`), **no SPARK009**, exactly three
  `Stamped indexName` console lines and no `Retargeted`/`Cleared`, and a model diff of precisely seven
  `useProjection` removals plus three `indexName` additions. `GetCommits` was stamped with nothing and
  `Commit.json`'s PO block gained no `queryType`/`indexName` — SP1a confirmed in practice.
- Hash movement: `files.{Account,Build,Repository}.json` moved, **`files.Commit.json` did not**; all four
  `entities.*` moved (the breadcrumb-attribute removal above); `modelHash` + `modelFiles` moved.
- The nine breadcrumb templates and three `[Reference]`-derived `query` values survived byte-identical, as
  did every curated `showedOn`/`order` trim — the `.54`/`.55` preservation guarantees doing their job.
- **M3's round-trip proof passed**: the hand-set `"query": "GetBuilds"` on `Commit.LatestBuildId` survived
  a second synchronize (which re-serialized it into its own property order, before `showedOn` — worth
  knowing, because a naive `grep -A1` for it reports a false negative). On `preview.53` this value would
  have been nulled on every run.
- `ParentSha`'s hand-set `"isReadOnly": true` survived untouched — the SP1 watch-item is clear.
- ng-spark bumped to `22.1.0` (D8): `package.json` + `package-lock.json`, zero source changes needed.
- **M5 found a pre-existing Spark bug, since fixed** (not a regression — reproduced on
  `master`/preview.53): the generic `GetRepositories` query 500'd because row security correlated
  projected rows via `session.LoadAsync<object>`, handing a `JObject` to a `Func<Repository, bool>` rule.
  Filed as [Spark#281](https://github.com/MintPlayer/MintPlayer.Spark/issues/281), fixed in
  `preview.57`, and re-verified here: the same request now returns 50 rows. Trigger and field data in §6.
  Everything else on the runtime surface checked out:
  the host boots with `RavenDB indexes created/updated from assembly: Coverage` and no errors,
  `GetAccounts`/`GetBuilds`/`GetCommits` all return rows, the wire model carries the three stamped
  `indexName` values and no `useProjection`, and the anonymous browse API answers 200.
- Full suite green: **141/141**, `ModelColumnGuardTests` included — passing *after* a synchronize for the
  first time, which is the preview.54 fix doing exactly what it promised.
- **SP4 executed, and it answers the deploy question: no index rebuild.** No side-by-side replacement
  index exists after running both the old and new code against one database, and every static index is
  `Normal` with zero errors and zero map errors at full entry count. The generated `FieldIndexing.No`
  calls reproduce the workaround's definition exactly.
- **The Angular client was verified through the host's own dev server** (never `ng build` — the host owns
  it): `/` serves `<app-root>` + `main.js` and the log carries zero TypeScript/bundler errors, so
  ng-spark `22.1.0` compiles against this app unchanged.
- The `.57` runtime logs `Row filter for Repository cannot compose into projection VRepository; falling
  back to post-materialization filtering with a batched reload` — the #281 path, now taken safely.

Five Spark issues filed from this repo shipped in four consecutive releases. The hop is `.53 → .57`:

| Release | Spark PR | Contents | Breaking for us? |
|---|---|---|---|
| **preview.54** | #277 (#274) | synchronize narrows-but-preserves hand-trimmed `showedOn` | no — pure fix |
| **preview.55** | #278 (#272 #273 #275 #276) | index coexistence; complex-field indexing; breadcrumb rework; synchronizer preservation | **yes ×2** |
| **preview.56** | #280 (#279) | query-declared index bindings; `IIndexRegistry` deleted | **yes ×1 (client-side only)** |
| **preview.57** | #282 (#281) | row security loads a projection's base documents as the entity type | no — pure fix, runtime only |

> Going straight to `.57` **skips** work: preview.55's `IIndexRegistry.GetRegistrationsForCollectionType` — the API we'd have had to reason about — is deleted again in `.56`. There is nothing to adopt from `.55`'s registry change at all.

> **The `.56` half is not a flag day; the `.55` half is.** A query contributes a hash line only when it
> carries an `indexName` (`ModelFileShape.cs:115-129`, explicit `continue` guard) and `useProjection` was
> never hashed — so the *file* hashes and the runtime survive un-synchronized, exactly as upstream
> documents. But this hop also removes the four class-level `[Breadcrumb]` attributes, which changes the
> **entity CLR-shape** hashes, so `--spark-verify-model` **exits 3 until the synchronize is run**
> (measured — see As-built). Plan the deploy as bump-and-synchronize together; there is no window where
> the new binaries run against the old committed model.

**Package availability, verified 2026-08-20:** `10.0.0-preview.57` is live on NuGet ✅ and
`@mintplayer/ng-spark@22.1.0` is live on npm and tagged `latest` ✅. (One of the three release runs,
`32350010621`, logged an `npm 404` on its publish step — a duplicate/racing attempt; the sibling run
published successfully. Ignore that log line.)

## 0. Decisions

- **D0 — one PR for the mechanical hop + the two free adoptions (D3, D4).** The `Commit` deliverable (D5) is deliberately a separate, later PR.
- **D1 — delete, don't port, the two preview.55 breaking usages.**
  - `Coverage\Indexes\GeneratedIndexes.ComplexFields.cs` (whole file): `Builds_Overview.OnInitialize()` covers `VBuild.Sessions/Coverage/Patch/Feedback/GateSnapshot/FlagCoverage`; `Repositories_Overview.OnInitialize()` covers `VRepository.LatestCoverage`. The generator emits all seven itself now (`GenerateIndexGenerator.Producer.cs:272-282`) and *then* calls `OnInitialize()` — the duplicate `Index()` breaks index creation. Its own header comment and three docs already scheduled this deletion.
  - The four class-level `[Breadcrumb(...)]` attributes: `Account.cs:10` `{Login}`, `Build.cs:10` `{CiRunId}`, `Commit.cs:10` `{Sha}`, `Repository.cs:9` `{FullName}`. Upstream `BreadcrumbAttribute` is now `[AttributeUsage(AttributeTargets.Property)]`, so these are **compile errors**, not warnings. All nine templates (four entity + five embedded) already live in `App_Data\Model\*.json`; all use scalar tokens only, so the new embedded-recursion behavior changes nothing rendered.
- **D2 — keep `ModelColumnGuardTests`, relabel it.** #274 is fixed in `.54`, so it should pass *after* a synchronize for the first time. Keeping it pins the upstream fix; only the doc comment (`ModelColumnGuardTests.cs:11-18`) changes from "delete once Spark preserves hand-edits" to "regression pin for Spark#274, fixed in preview.54".
- **D3 — adopt #275 with `Commit.LatestBuildId` → `"query": "GetBuilds"`.** The field holds a Build document id (`Commit.cs:68`, `Commit.json:105-118`) with no `[Reference]`; hand-setting its `query` in JSON makes it a navigable lookup with zero CLR change. Previously wiped on every synchronize, which is why it was never done.
- **D4 — no `[DefaultIndex]` anywhere, and specifically NOT on `Commits_ByRepository`.** Verified election rules (`IndexCatalog.cs:174-203`, run in `Freeze()`): candidates are **projection-bearing** entries only; 0 candidates → no default, no error; exactly 1 → implicit default, marker unnecessary. Account/Build/Repository each have exactly one (auto-emitted by `[GenerateIndex]`); `Commit` has zero. **Rule 1 is a trap worth stating: `[DefaultIndex]` on a projection-less index throws at startup** ("has no effect: the index has no `[FromIndex]` projection"), so it must never be added to `Commits_ByRepository`.
- **D5 — the `Commit` question is now genuinely unblocked, and the answer is still "not coexistence".** Preview.56 invalidates the two mechanical premises D5 rested on: `Commits_ByRepository` is projection-less, therefore invisible to the default election (no marker, no ambiguity, **SPARK009 does not fire**), and a declared `indexName` is now authoritative. But the two premises that actually decide it stand: all nine hand-written call sites still need the `AuthoredAt ?? FirstSeenAtUtc` coalesce and `Coverage != null`, and the production commit list is `Custom.Repository_Commits`, which materializes in memory and gains nothing from any index binding. Coexistence would pay a second index over the heaviest write collection to improve a reference-picker. **The standing step-6 route wins** (persist the two computed fields, generate, delete the hand-written index): one index instead of two, SP4 of `adopt-generated-indexes.md` dissolved, and the read-only call sites move from `.OfType<Commit>()` (index seek + document load per row) to `ProjectInto<VCommit>()` served from `StoreAllFields`. **Out of scope here — its own PR**, planned in §5.
- **D6 — no #276 cleanup exists.** All four `Database.*` sources pair 1:1 with `CoverageSparkContext.cs:9-12`; no duplicates, no rename in the model's git history. Prophylactic only.
- **D7 — no registry API exposure.** Repo-wide grep: zero references to `IIndexRegistry`/`GetRegistrationForCollectionType`/`IsProjectionType`; nothing suppressed the removed `SPARK_INDEX_004`. The deletion is a no-op here.
- **D8 — bump `@mintplayer/ng-spark` to `22.1.0` for lockstep, not for necessity.** `22.1.0` is the client
  that ships with preview.56, and its only change is dropping `useProjection?: boolean` from the `SparkQuery`
  TS model. `ClientApp\src` has **zero** hits for `useProjection`, `SparkQuery`, or any query-model
  construction — it uses `models`/`renderers`/`services`/`pipes`/`po-detail`/`routes`, plus `ng-spark-auth`
  (already `22.1.0`) — so functionally this is a type-surface no-op. Bump it anyway: server and client ship
  in lockstep here, `ng-spark-auth` is already there, and after M2 our model JSON no longer carries a field
  the old client type still declares. **Mechanics matter:** `package.json:34` already reads `^22.0.11`,
  which *admits* `22.1.0`, so editing the manifest alone changes nothing — the real pin is
  `package-lock.json:23-24` and `:1834-1836`. Run `npm install @mintplayer/ng-spark@22.1.0` in
  `Coverage\ClientApp` and commit **both** files; CI runs `npm ci`, which installs strictly from the lock
  and fails on manifest/lock disagreement. Do **not** run `ng build`/`ng serve` — `Program.cs:300` uses
  `spa.UseAngularCliServer(npmScript: "start", …)`, so the host owns the dev server and CI's
  `npm ci && npx ng build` is the gate.
- **D9 — `useProjection` is not hand-edited.** All seven occurrences stay until synchronize removes them on the read→re-serialize round-trip. Verified safe: both readers use `JsonSerializerOptions` without `UnmappedMemberHandling`, so System.Text.Json defaults to `Skip`; the field was never structural, so removal is hash-neutral.

## 1. Spikes

### SP1 — first-synchronize forensics (the one gate that can block the PR) — ✅ EXECUTED 2026-08-20, clean

Three releases of behavior change converge on one run. **Method:** run the Synchronize profile on the bumped branch, capture full console output, `git diff` the model + hashes. **Outcome: every expectation below was met exactly, with no unexplained line.** Expected list, as authored:

- Console: exactly three `Stamped indexName '…' on query '…'` lines; **no** `Retargeted` or `Cleared` lines.
- Removed: the seven `"useProjection": false` lines (`Account.json:104`, `Build.json:358,373`, `Commit.json:217,236`, `Repository.json:238,253`).
- Added: `"indexName"` on exactly three `Database.*` queries — `GetAccounts` → `Accounts_Overview`, `GetBuilds` → `Builds_Overview`, `GetRepositories` → `Repositories_Overview`.
- **`GetCommits` gains nothing** (see SP1a). `Custom.*` queries (`Commit_Builds`, `Repository_Commits`, `Account_Repositories`) untouched.
- `modelHashes.json`: `files.{Account,Build,Repository}.json` + `modelFiles` + `modelHash` move; **`files.Commit.json` and every `entities.*` byte-identical**.
- Survive unchanged: the nine breadcrumb templates, the three `[Reference]`-derived `query` values (e.g. `Commit.json:175`), every curated `showedOn`/`order` trim.

**Decision rule:** any diff line or warning not in that list blocks the PR and goes upstream. One known watch-item: `ParentSha` carries a hand-set `"isReadOnly": true` on a settable property (`Commit.json:143`) while `isReadOnly` derives from `!CanWrite` — its preservation provenance is unverified, so check whether it survives.

### SP1a — why `GetCommits` gets nothing (resolved, recorded)

Stamping reads `indexCatalog.GetDefaultForCollectionType` (`ModelSynchronizer.cs:105-107`), whose candidate set is projection-bearing entries. `Commits_ByRepository` has no `[FromIndex]` companion — confirmed by a live verify run logging `Registered index: Commits_ByRepository (Collection: Commit)` with no matching `Registered projection:` line — so the default is `null`, the loop hits `if (indexName is null) continue;`, and `WhenWritingNull` emits no property. `Commit.json`'s PO block likewise keeps having no `queryType`/`indexName`, exactly as today. **This retires the D5 degradation risk entirely**: the generic `Database.Commits` grid is never bound to the narrow hand-written index.

### SP2 — property-level `[Breadcrumb]` persistence vs the never-store-percent invariant (gates M6)

Unchanged by `.56`. The marker's computed property "persists into the document JSON", which contradicts `CoverageSummary`'s explicit invariant (`CoverageSummary.cs:4-5`: percentages derived by consumers, never stored, so they can't drift) and re-opens the computed-property trap documented twice here (`Build.Run`, `Commit.Date`). **Method:** read the shipped generator/synchronizer, then a throwaway branch adding a `Percent` marker to `CoverageSummary`: (1) when is the value written — every save, or index-time? (2) do pre-upgrade documents need a backfill (pattern: `M_202608190900_BackfillBuildRun`)? (3) does the `{Name}Sort` companion change `Builds_Overview`/`Repositories_Overview`'s definition and force a full Builds re-index? (4) `CoverageSummary.json:6` already carries breadcrumb `"{LinesCovered}"` — which wins, and does the synchronizer rewrite it? **Decision rule:** adopt only if drift is impossible or a one-shot backfill fully repairs history; otherwise file upstream and keep percentages derived.

**Note:** SP2 shares its root question with the `Commit` deliverable's blocking spike (§5, "is a get-only CLR property persisted into the document?"). Answer it once, in SP2, and both plans resolve.

### SP3 — RETIRED, superseded by shipping

SP3 asked whether the generic path resolved via the collection-type default or the projection's own index. Answered by reading preview.55 (it did resolve ambiently, *and* silently overrode a declared `indexName`), filed as [Spark#279](https://github.com/MintPlayer/MintPlayer.Spark/issues/279), and **fixed in preview.56** — resolution is now `query.indexName` → entity-file binding → raw collection, with an unknown name raising an error instead of silently querying the raw collection. Record: `docs/spark-issue-279-PRD.md`.

### SP4 — index-definition parity → rebuild cost

The workaround produced healthy production indexes; if the generated definitions match byte-for-byte, deployment is a no-op. **Method:** compare the generated index definitions (preview.53 + workaround vs preview.56) or inspect side-by-side in RavenDB Studio after a local run. **Decision rule:** identical → deploy freely; different → accept one side-by-side rebuild of `Builds_Overview`/`Repositories_Overview` (loses nothing — they were healthy — but time the deployment window).

**✅ EXECUTED 2026-08-20 — identical, deploy freely.** The local database served both the preview.53
(workaround) and the preview.56/.57 (generator-emitted) hosts in this session. Afterwards
`/databases/Coverage/indexes/stats` reports **no side-by-side replacement index** and every static index
`state=Normal, ErrorsCount=0, MapErrors=0` with full entry counts (`Repositories/Overview` 160,
`Builds/Overview` 8, `Accounts/Overview` 2, `Commits/ByRepository` 23). So the generator's own
`Index(field, FieldIndexing.No)` calls produce a definition equal to the workaround's — **no rebuild on
deploy**, and the complex fields index cleanly (zero map errors is the direct refutation of the #273
Corax fault on this data).

## 2. Milestones

### M1 — package bump + mandatory deletions 🔑

- `Coverage\Coverage.csproj:20-26,28` (eight refs), `Coverage.Library\Coverage.Library.csproj:11`, `Coverage.Tests\Coverage.Tests.csproj:14`: `10.0.0-preview.53` → `10.0.0-preview.57`. `Coverage.Tests` holds no Spark runtime package, so there is no `MintPlayer.Spark.Testing` to bump.
- npm (D8): `cd Coverage/ClientApp && npm install @mintplayer/ng-spark@22.1.0`; commit `package.json` **and** `package-lock.json`. Sanity-check that `npm ci` resolves.
- Delete `Coverage\Indexes\GeneratedIndexes.ComplexFields.cs`.
- Remove the four `[Breadcrumb(...)]` attributes; check whether `Commit.cs`'s `using MintPlayer.Spark.Abstractions;` becomes unused (Commit has no `[GenerateIndex]`, unlike the other three).
- `dotnet build Coverage.slnx -c Release`. Baseline today: succeeds with exactly two unrelated warnings (`ASPDEPR005` Program.cs:26, `ASP0014` Program.cs:269). Expect those plus ~7 `SPARK_INDEX_010` (six VBuild complex fields + `VRepository.LatestCoverage`) — **confirm `FlagCoverage` (`Dictionary<string, CoverageSummary>`) is among them**; a mismatch with the old workaround list means the classifier disagrees and needs upstream attention before merging. **`SPARK009` must not appear.**

### M2 — verify-before, synchronize, verify-after (executes SP1) 🔎

- `--spark-verify-model` **before** synchronizing → **expect exit 3**, reporting exactly four moved *entity* hashes (`Account`, `Build`, `Commit`, `Repository`) and **no** moved file hashes. That signature is the `[Breadcrumb]` removal and nothing else; any moved *file* hash at this point is outside this analysis and blocks.
- `dotnet run --project Coverage --launch-profile Synchronize`; audit against SP1's expected diff; commit `App_Data/Model/*.json` + `App_Data/modelHashes.json`.
- `--spark-verify-model` again → exit 0 required (CI gate, `.github\workflows\ci.yml:31`).

### M3 — first #275 adoption: `Commit.LatestBuildId` lookup 💄

- `Coverage\App_Data\Model\Commit.json` (~:105-118): set `"query": "GetBuilds"` on the `LatestBuildId` attribute.
- Round-trip proof: re-run Synchronize and assert the value survives — this *is* the #275 guarantee, and on preview.53 it would have been nulled.
- Eyeball the generic commit PO page: the attribute renders as a navigable Build lookup.

### M4 — housekeeping 🐞

- Relabel `ModelColumnGuardTests.cs:11-18` per D2.
- `docs\adoption-findings.md` §4: mark upstream asks shipped (#274 → .54; #272/#273/#275/#276 → .55; #279 → .56).
- `docs\spark-handoff.md:148-202`, `docs\adopt-generated-indexes.md:7-9`: mark the complex-field workaround deleted; record the D5 stance and that #272's tiebreaker ask is superseded by #279.
- `docs\PLAN.md:266-271`: tick the upstream-dependency lines.

### M5 — sweep 🔑

- Full suite once, at the end: `dotnet test Coverage.Tests/Coverage.Tests.csproj -c Release`. `ModelColumnGuardTests` must pass *after* the M2 synchronize — first time ever.
- Run the host (`--launch-profile https`) and **watch startup output**. This matters more than usual: `SparkMiddleware.cs:518-529` catches index-creation failures **per assembly and only logs them**, so a botched M1 deletion would silently leave *all four* Coverage indexes undeployed behind one console line. Expect four `Registered index:` lines and three `Registered projection:` lines, and no `Error creating RavenDB indexes from Coverage`. Then eyeball breadcrumbs on repository/commit/build pages and the coverage-bar / AsDetail columns.

### M6 — (conditional on SP2) sortable coverage percent 💄

Only if SP2's decision rule passes: `[Breadcrumb]` marker property on `CoverageSummary`, backfill if required, sort assertions on `Repositories_Overview`/`Builds_Overview`, UI check that the coverage column header orders by percent. Otherwise record the SP2 outcome here and file the upstream ask.

## 3. New failure modes introduced by .56 (watch, don't pre-empt)

Coverage authors no query-level `indexName` and its three `queryType` values all resolve, so none of these should fire — but they are new, and worth recognizing in a log:

- **Unknown `indexName`** → `InvalidOperationException` at **query execution time**, not startup: *"Query '{name}' resolves to index '{indexName}', but no deployed index has that name."* Note the catalog never cross-checks model files at boot, so this surfaces per-request.
- **Unresolvable `queryType`** on the PO-list path → `InvalidOperationException` at request time.
- **Ambiguous default** (≥2 projection-bearing indexes, ≠1 marker) → throws at startup *and* exits 2 from synchronize/verify.
- **`[DefaultIndex]` on a projection-less index** → throws (D4's trap).
- **Duplicate index class names** → now throws at registration (was first-wins). Coverage's four names are distinct.
- **Sort column missing from the resolved projection** → console warning + column skipped, rows keep index order (not an exception).

## 4. Out of scope, recorded

- **Build derived-state index** — #272/#279 unblock a second, purpose-tuned index over `Build.Sessions` for a filterable classified state (`adopt-generated-indexes.md:234-236` recorded the old collision). No demand — `ClassifyState` is in-process only. Unblocked ≠ wanted.
- **Sha-valued lookups** (`Build.DeclaredBaseSha`, `Commit.ParentSha`, `Repository.LatestCoverageSha`) — a hand-set `query` helps only if the picker matches on `Sha` rather than document id. Speculative.
- **`Commit.Branch` / `BuildSession.Flags` lookups** — no Branch/Flag entity; would need a `Custom.*` distinct-values target. Speculative.
- **`[Breadcrumb]` on `BuildSession`** — sorting builds by first session id has no user value. `PatchCoverage`/`GateSettings`/`BuildFeedback` are PO-only, never grid columns.
- **Bumping ng-spark to 22.1.0** — blocked upstream (D8), and a no-op for us.

## 5. Next deliverable (separate PR): `Commit` → one index

Recorded here so the sequencing is explicit; full design belongs in its own doc when picked up.

- **Blocking spike (shared with SP2): is a get-only CLR property persisted into the RavenDB document?** The evidence conflicts head-on — Spark's `release-notes-preview-55.md:69-73` says computed get-only properties persist (the breadcrumb mechanism depends on it), while this repo's `Build.cs:30-36` says a get-only CLR property "does not exist" server-side, and `M_202608190900_BackfillBuildRun` exists precisely because `Run` was never in the document. **Method:** run the host, open a `Commits/*` document in Raven Studio and look for `Date`; cross-check a `Builds/*` document for `Run`.
  - **Yes** → `[GenerateIndex]` on `Commit` + a two-line `HasCoverage => Coverage != null` get-only property + a backfill. Single-writer discipline cost vanishes: values recompute on every serialize, so they cannot drift.
  - **No** → the docs' original shape: real settable `Date`/`HasCoverage` fields with writers at `UploadsController.cs:67`, `GitHubEventsRecipient.cs:157,260`, `BuildFinalizer.cs:38`, plus the backfill.
- Then: delete `Commits_ByRepository.cs`; rewrite the nine call sites (the five read-only ones to `ProjectInto<VCommit>()` — **mandatory, or index-computed fields come back null with no error**; `DeletePullRequestBuildsRecipient.cs:34` and `CommitActions.cs:45` keep `.OfType<Commit>()` because they mutate/hydrate); **retarget `Coverage.Tests\CoverageRavenTest.cs:78`, which anchors on `typeof(Commits_ByRepository).Assembly`** or the test base stops compiling; backfill migration; extend `ModelColumnGuardTests` with a `Commit` case pinning `Sha, Branch, Coverage, CoverageDelta, Date`.
- **Two traps found while assessing it:** (1) `CoverageDelta` is `[JsonIgnore]` and the generator has no `JsonIgnore` handling — it *is* mapped into `VCommit` and indexes as null; the tempting `[IgnoreForIndex]` fix would narrow its `showedOn` to `PersistentObject` and **delete the Δ column from the repository page's grid**, since `showedOn` is per-entity and shared by both queries. Leave it mapped-and-null. (2) Migrations run *after* index creation (`Program.cs:107-109`), so `Commits_Overview` starts mapping documents that still lack the new fields — self-healing once the patch bumps change vectors, but there is a transient window of wrong badge/list ordering. Run the patch pre-deploy or accept a timed window.

## 6. Upstream (Spark)

- **✅ FIXED — [#281](https://github.com/MintPlayer/MintPlayer.Spark/issues/281) →
  [PR #282](https://github.com/MintPlayer/MintPlayer.Spark/pull/282) → `10.0.0-preview.57`, adopted here
  the same day.** `RowSecurity` now loads the base documents as the declared entity type (one
  `ReflectionCache`-cached generic close, still one batched request), and fixes the identical defect in
  `RedactAsync` — which this repo was also exposed to, since `RepositoryActions.GetProtectedAttributesAsync`
  redacts `BadgeToken`. Verified against the real failing request: `GetRepositories/execute` returns 50
  rows where it previously 500'd, **including the poisoned document**.
  **The upstream analysis corrected my report on one point that matters:** rule + projection is *not*
  sufficient. `LoadAsync<object>` resolves the CLR type from the `Raven-Clr-Type` metadata and only falls
  back to `JObject` when that metadata is absent or unresolvable. Measured in this repo's dev database:
  **159 of 160 `Repositories` documents carry `"Raven-Clr-Type": "Coverage.Entities.Repository,
  Coverage.Library"`; exactly one does not** — `Repositories/999001`, the hand-seeded `acme/demo` fixture
  from 2026-08-12 (`adopt-spark-generic-ui.md:413`), written by a raw put rather than the .NET client, so
  it carries only `@collection`. `ApiTokens/dd735caa…` is the same story. One such row is enough to fail
  the whole page, which is why the grid broke completely rather than partially. Original report follows.
  `GET
  /spark/queries/{GetRepositories}/execute` returns **HTTP 500**:
  `System.ArgumentException: Object of type 'Newtonsoft.Json.Linq.JObject' cannot be converted to type
  'Coverage.Entities.Repository'` from `RowSecurity.FilterAsync` → `ResolveEffectiveRuleAsync`.
  **Verified identical on `master`/preview.53**, so it is latent, not a regression — but it is real and
  worth filing.
  - **Mechanism** (`libs/spark/MintPlayer.Spark/Services/RowSecurity.cs:181-216`): when the query
    projects (`resultType != entityType`), the filter correlates each projected row back to its document
    with one batched `await session.LoadAsync<object>(ids)` at `:191`. RavenDB has no target type there,
    so it materialises **`JObject`s**; the loop then invokes the compiled rule — a
    `Func<Repository, bool>` — with that `JObject` at `:214`, and reflection refuses the argument.
  - **Trigger is precise: an entity with BOTH a `GetRowFilterAsync` rule AND a `[FromIndex]`
    projection, queried through its generic `Database.*` root.** In this repo only `Repository`
    qualifies. `Commit` has a rule but no projection (works); `Build`/`Account` have projections but no
    rule (work) — all three verified returning rows.
  - **Why production never hit it:** the repository grids use `Custom.Account_Repositories`, which
    returns `IRavenQueryable<Repository>` — the *base* entity — even though it queries through
    `Repositories_Overview` (`RepositoryActions.cs:50-55`). That makes `projecting` false and skips the
    faulty path entirely. Only the lightly-used generic root projects to `VRepository`.
  - **Fail-closed, not a leak:** the request errors; no unfiltered rows are returned. (The neighbouring
    "no `Id` on the projection" branch at `:165-173` is likewise deliberately empty-not-open.)
  - **Suggested fix:** load as the entity type rather than `object` — a reflective
    `session.LoadAsync<TEntity>(ids)` closed over `entityType` (the same reflective-generic-invoke
    pattern `QueryExecutor.ApplyIndexWithType` already uses), so the documents deserialize into
    `Repository` and the rule receives what it declares. A regression test needs an entity carrying a
    row rule *and* a projection — the existing `RowFilterPushdownTests` fixtures cover rule-without-
    projection, which is why this survived.

- **#272's tiebreaker ask is superseded.** [#279](https://github.com/MintPlayer/MintPlayer.Spark/issues/279) → PR #280 → preview.56 deleted `IIndexRegistry` outright, so no `IsDefault` marker on `[GenerateIndex]` is needed to make coexistence safe. Shipped record: `docs/spark-issue-279-PRD.md`.
- **Resolved, no action:** release run `32350010621`'s failed `Push ng-spark to NPM` step was an expired
  npm token, not a racing publish path. The package published from a later run. Noted here only because
  the red run misled this plan once — a failed publish job is not evidence the package is missing; check
  the registry.
- **Filed 2026-08-20, both open:**
  [#284](https://github.com/MintPlayer/MintPlayer.Spark/issues/284) — grid columns are per-entity, so a
  column cannot be shown on one query's grid and hidden on another (this repo's account-scoped vs global
  repositories views; sharpened by #279, since a query bound to a non-default index still draws the
  entity's column set over that projection's rows).
  [#285](https://github.com/MintPlayer/MintPlayer.Spark/issues/285) — the row filter cannot push down into
  a projection query, so a row-scoped type reads its whole collection per page. `Repository` is exactly
  that shape here (visibility rule + `[GenerateIndex]` projection): fine at 160 documents, not at scale.
  It is also the reason the untyped base-document reload behind #281/#283 has to exist at all.
- **Open upstream and worth watching:**
  [#283](https://github.com/MintPlayer/MintPlayer.Spark/issues/283) — `BreadcrumbResolver`'s untyped loads
  gate on `IsAllowedAsync(typeof(JObject), …)`, which returns **true**, bypassing the row-level Read rule.
  Where #281 failed closed, this fails **open**. Our exposure runs through `Commit.Repository` /
  `Repository.Account` breadcrumbs (`Repository`'s template is `{FullName}`), and only for documents whose
  `Raven-Clr-Type` does not resolve — the class of document repaired in the dev database on 2026-08-20.
  **Interim mitigation: scan production for documents missing that metadata**; if there are none, there is
  no exposure until the fix lands.
- **Possible ask, pending SP2:** breadcrumb-persistence semantics — when the marker property's value is written, and the backfill story for pre-existing documents.
