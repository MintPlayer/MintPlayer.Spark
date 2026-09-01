# Adopting `[GenerateIndex]` — RavenDB indexes generated from the entities

**Status**: ✅ ADOPTED 2026-08-19 for `Build`, `Repository` and `Account` (steps 1–4 below, plus
the generic-surface cutover, which turned out to come for free — see "As built"). `Commit` stays
hand-written as §2 argues. Two upstream defects found on the way:
[#272](https://github.com/MintPlayer/MintPlayer.Spark/issues/272) (registry rebinding) and
[#273](https://github.com/MintPlayer/MintPlayer.Spark/issues/273) (Corax faults on generated complex
fields).

**Update 2026-08-20 — both shipped, and the local workaround is gone.** #273 shipped in
`10.0.0-preview.55`: the generator now classifies complex fields itself and emits
`Index(field, FieldIndexing.No)`, so `Coverage/Indexes/GeneratedIndexes.ComplexFields.cs` was **deleted**
(keeping it would throw at startup on a duplicate `Index()` call). #272 shipped in `.55` and was then
superseded by [#279](https://github.com/MintPlayer/MintPlayer.Spark/issues/279) in `.56`, which deleted
`IIndexRegistry` outright in favour of query-declared index bindings — **the one-index-per-entity ceiling
that §2 argues against no longer exists.** §2's conclusion still stands on its own merits (the nine call
sites need the coalesce; the production commit grid is a `Custom.*` source no index binding helps), so
`Commit` coexistence is unblocked but deliberately not taken; the target remains one index via step 6.
See [adopt-spark-preview-57.md](adopt-spark-preview-57.md) §5.

## As built (2026-08-19)

- `preview.53`; `[GenerateIndex]` on `Build`, `Repository`, `Account`; `CoverageSparkContext` made
  `partial`. The generic grids now run through `Builds_Overview`/`Repositories_Overview`/
  `Accounts_Overview` automatically — registering a `[FromIndex]` projection reroutes the entity's
  generic query, so §3's "expensive half" was not optional and not separate: it happened the moment
  the attribute landed. What §3 predicted became three concrete fixes:
  - **`Build.Run` is now a stored field** (`Build.ComposeRun`, one writer at build creation) with a
    backfill migration `M_202608190900_BackfillBuildRun` — a get-only property would have indexed as
    null and blanked the grid column for all history.
  - **`Repository.BadgeToken` got `[IgnoreForIndex]`**: synchronize marks every projected field
    queryable (`showedOn` is re-derived from CLR presence on each run — hand-edits verified not to
    survive), and that would have put a live badge token in the anonymous `/spark` grid. Membership
    is the only durable lever.
  - **Complex fields** (`Sessions`, `Coverage`, `LatestCoverage`) fault Corax under default indexing
    — `GeneratedIndexes.ComplexFields.cs` sets `FieldIndexing.No` through the `OnInitialize()` seam;
    they stay stored, so the AsDetail renderers keep working. Filed as #273.
- Every hand-written query on the three collections now names the index via
  `Query<Entity, TIndex>()` (documents back, signatures unchanged) — the finalize cron, all
  `FullName`/`OwnerLogin`/`Login` lookups, the badge path, both custom queries.
- The row filter still composes: `RepositoryActions.GetRowFilterAsync` stays typed on `Repository`
  and the generic query's element type stays the entity on the pushdown path.
- Tests: the shared `CoverageRavenTest` base creates the assembly's indexes per store (what
  `UseSpark()` does), because index-named queries throw `IndexDoesNotExistException` on a bare store
  instead of falling back to an auto-index. 79/79 green; `--spark-verify-model` exit 0.

The sections below are the investigation as written before adoption, kept for the reasoning.

**Why now.** Spark [#269](https://github.com/MintPlayer/MintPlayer.Spark/pull/269) (closing
[#210](https://github.com/MintPlayer/MintPlayer.Spark/issues/210)) adds a source generator that emits
a RavenDB index, an index-entity projection and query roots on the `SparkContext` from a
`[GenerateIndex]` attribute on an entity. Merged 2026-08-18T20:19:23Z; every `MintPlayer.Spark.*`
package published `10.0.0-preview.53` five minutes later. This repo pins `preview.52`.

**The problem it addresses here.** Coverage has exactly one static index — `Commits_ByRepository` —
and *every other query in the app runs against a RavenDB auto-index*. That includes the single
hottest query in the codebase, a cron that fires every sixty seconds forever, and four collections
exposed anonymously through Spark's generic query API where the client chooses the filter and sort.
None of that is visible in code review: an auto-index is created silently on first use and works, so
the cost is only ever paid in index count, indexing throughput and cold-query latency.

---

## 1. What the generator actually does

Verified against the merged PR's sources, snapshot tests and the `Demo/Fleet` conversion — not
against a build of this repo.

### The attribute

`MintPlayer.Spark.Abstractions.GenerateIndexAttribute`, class-targeted, no constructor parameters:

| Property | Default | Effect |
|---|---|---|
| `IndexName` | `{Plural}_Overview` | index class name. **Renaming re-indexes the database** — RavenDB identifies an index by its class name. |
| `IndexEntityName` | `V{Entity}` | projection class name |
| `Description` | — | emitted as `[Description]`, documentary only |

Two companion property attributes ship with it: `[Search]` (string-ish only; sets
`FieldIndexing.Search` **and** emits a `{Name}Sort` companion) and `[IgnoreForIndex]` (out of the
index, still in the Spark model).

### Where the code lands — and why #263 does not bite

The attribute lives in `MintPlayer.Spark.Abstractions`, which `Coverage.Library` **already
references**. The generator is `MintPlayer.Spark.SourceGenerators`, which `Coverage` and
`Coverage.Tests` **already reference**, already in the required `PrivateAssets="all"` +
`IncludeAssets="…analyzers…"` form that `spark.targets` enforces via `SPARK001`/`SPARK002`/`SPARK003`.

The generator does not use `ForAttributeWithMetadataName`. It pairs a syntax provider for
source-declared entities with a `CompilationProvider` metadata walk over
`compilation.SourceModule.ReferencedAssemblySymbols`, filtered to assemblies that themselves
reference `MintPlayer.Spark.Abstractions`. Incrementality is deliberately traded away there and
recovered by value-comparing the result.

The consequence that matters: **entities live in `Coverage.Library`, generated indexes land in
`Coverage`** — namespace `{RootNamespace}.Indexes`, i.e. `Coverage.Indexes`, the same namespace as
the hand-written `Commits_ByRepository`, and the same assembly. `UseSpark()` resolves index
assemblies with `Assembly.GetEntryAssembly()` first, so deployment stays automatic and
`spark.AddIndexesFrom(...)` stays unnecessary. `adopt-spark-generic-ui.md:554-560` predicted the
opposite hazard — that moving an index into `Coverage.Library` walks into
[#263](https://github.com/MintPlayer/MintPlayer.Spark/issues/263), silently uncreated index and
unregistered projection. The generator sidesteps it by construction: the *attribute* moves to the
library, the *index* does not.

Attaching the generator to `Coverage.Library` instead would reintroduce exactly that failure. It must
stay on the app.

### The generated shape

```csharp
namespace Coverage.Indexes
{
    [FromIndex(typeof(Builds_Overview))]
    public partial class VBuild
    {
        public string? Id { get; set; }          // declared, never mapped — Raven supplies it
        public string? Commit { get; set; }
        public string Status { get; set; } = default!;
        // …one property per Spark model property
    }

    public partial class Builds_Overview : AbstractIndexCreationTask<Build>
    {
        public Builds_Overview()
        {
            Map = builds => from build in builds
                            select new VBuild { Commit = build.Commit, Status = build.Status, … };
            StoreAllFields(FieldStorage.Yes);
            OnInitialize();
        }

        partial void OnInitialize();
    }
}
```

Plus a query root per pair on the app's `partial SparkContext`:

```csharp
public IRavenQueryable<VBuild> VBuilds => Session.Query<VBuild, Builds_Overview>();
```

`StoreAllFields(FieldStorage.Yes)` is unconditional and is described upstream as mandatory rather
than conventional — without it a projection-only field returns null through `ProjectInto` with no
error and no index fault. That is the same trap `CLAUDE_steve.md` records and that `PLAN.md:44`
already warns about.

### Membership: opt-out, not opt-in

Every public instance property with a getter that is not `Id` and not `[IgnoreProperty]` is indexed,
**including inherited ones**. There is no expression escape hatch, by explicit upstream design: every
map assignment is literally `entity.PropertyName`. `OnInitialize()` cannot help — a generator adds
members to a partial class, it cannot add statements to a constructor you wrote.

`TranslatedString` is the one fan-out (replaced by one field per language from `culture.json`); we
have none, so `App_Data/culture.json` and its `AdditionalFiles` line are not needed here.

---

## 2. What this repo can and cannot generate

### `Commit` — not yet, for two independent reasons

1. **One index per entity, enforced as a build error — but the enforcement is papering over a
   registry defect, not a RavenDB one.** `SPARK_INDEX_004` fails the build when an entity that
   already has a hand-written index gets `[GenerateIndex]`, so `[GenerateIndex]` on `Commit` does not
   compile while `Commits_ByRepository` exists.

   RavenDB itself is perfectly happy with several indexes over one collection. The ceiling is
   `IndexRegistry._byCollectionType`, a `Dictionary<Type, IndexRegistration>` that structurally
   cannot hold more than one index per entity — and `RegisterIndex` guards only `_byIndexName`, then
   assigns `_byCollectionType[collectionType]` unconditionally
   ([`IndexRegistry.cs:88`](https://github.com/MintPlayer/MintPlayer.Spark/blob/023ec43b097a338e2dcc801119a32ec4d6823185/libs/spark/MintPlayer.Spark/Services/IndexRegistry.cs#L88)).
   So a second index does not "get skipped", as the diagnostic's own doc-comment claims — it
   *rebinds the collection*, and the winner is decided by `Assembly.GetTypes()` order. Filed upstream
   as [Spark#272](https://github.com/MintPlayer/MintPlayer.Spark/issues/272) 🟩, proposing a
   deterministic first-wins guard plus an explicit default so coexistence stops being a diagnostic.

   Two consequences for us. The diagnostic is an *analyzer* diagnostic, so `.editorconfig` can switch
   it off — and hand-written queries never consult the registry, so
   `session.Query<VCommit, Commits_Overview>()` would work regardless. Suppressing it is therefore
   possible and is still the wrong move: it buys an index none of our existing call sites can use
   (see 2), doubles indexing work on the heaviest write path, and leaves `Database.Commits` bound to
   whichever index reflection happened to reach last.
2. **Three of its four indexed fields are not projections.** `AuthoredAt` is a coalesce
   (`commit.AuthoredAt ?? commit.FirstSeenAtUtc`), `HasCoverage` is a null test
   (`commit.Coverage != null`), and only `Repository` and `Branch` are straight copies
   (`Commits_ByRepository.cs:26-32`). A generator that maps `entity.Property` cannot express either.

The coalesce is the one to be loudest about, because losing it is a **correctness** regression that
is silent. Upload-only commits never receive a push webhook and therefore have `AuthoredAt` null;
those are the *majority* of commits, since OIDC auto-provisioned repositories have no webhook path at
all. An index that mapped the raw property would sort every one of them to one end, and six
request-path call sites order by it.

So `Commit` stays hand-written **for now**. That is not a gap in the generator — it is the case the
generator explicitly leaves to hand-written indexes, and the `[Search]`/`IndexSearchFields()` partial
seam exists for exactly this: keep the map, gain the companions.

**The route that removes both blockers at once is to stop computing the two fields at index time and
persist them instead.** `Date` already has a single well-defined write point — `FirstSeenAtUtc` is
stamped at document creation in both the webhook and the upload path (`PLAN.md:167`), which is what
the coalesce exists to paper over — and `HasCoverage` has one writer, `BuildFinalizer`, the only
thing that assigns `Commit.Coverage`. Materialize both and every field becomes a plain projection:
`[GenerateIndex]` compiles with no suppression and no dependence on #272, `Commits_ByRepository` is
*deleted* rather than duplicated, there is exactly one index per collection, and **SP4 dissolves** —
the generic grid can finally sort on `Date` because `Date` is a real indexed field.

Cost is one Spark migration plus single-writer discipline on two fields, which is the pattern N4 just
established for `ParentSha`. It is more work than suppressing a diagnostic, and it is the only option
that ends with fewer hand-written indexes than we started with.

### `Repository` — yes, and it is the biggest win

Every property is a plain projection, including the nested `LatestCoverage`. The value is not one
query but three separate pressures on the same collection:

- `ResolveVisibleRepository` (`BrowseController.cs:418`) opens **all nine** browse endpoints with a
  `FullName` equality. Five further sites do the same lookup (`BadgeController.cs:34`,
  `RepoSettingsController.cs:28`, `TokensController.cs:50`, `UploadsController.cs:307`).
- The `/spark` visibility row filter — `!IsPrivate || OwnerLogin.In(allowed)`
  (`RepositoryVisibility.cs:35-36`) — is ANDed onto every generic Repository query, so the index must
  carry `IsPrivate` **and** `OwnerLogin` or pushdown breaks for the whole surface. A generated index
  carries both, since it carries everything.
- `OwnerLogin` filters on the account page and on every signed-in page load (`MeController.cs:49`).

### `Build` — yes, and it is what prompted this

`FinalizeBuildsCronJob.cs:32-36` filters `Status`, `LastUploadAtUtc` and `CreatedAtUtc` every sixty
seconds, forever; `BuildActions.cs:51` filters `Commit` on the commit detail grid.

**`Build.Run` must be marked `[IgnoreForIndex]` before the attribute goes on.** It is a get-only
computed property, `$"{CiRunId}.{CiRunAttempt}"` (`Build.cs:30`), and it is a `showedOn: "Query"`
column in `Build.json` — so it passes the generator's membership test and would be emitted as
`Run = build.Run` into a map that runs server-side against a JSON document where no such field
exists. `[JsonIgnore]` would not have saved us: it is Newtonsoft's, and the generator reads
`[IgnoreProperty]`/`[IgnoreForIndex]` only.

`Build.Sessions` (a `List<BuildSession>`) and `Build.Coverage` map verbatim and are then *stored* by
`StoreAllFields`. That is correct but not free — decide deliberately whether `Sessions` earns
`[IgnoreForIndex]`, since nothing filters or sorts on it and it is the largest field on the document.

`Build.ClassifyState` (`Build.cs:68`) is a static method, not a property, so the generator ignores it.
If the derived state ever becomes filterable it needs a hand-written map over the embedded
collection — which would then collide with the generated index under the one-index-per-entity rule.

### `Account` — yes, cheap

Small collection, but on the signed-in hot path, and `GitHubAccessService.cs:230` already pays a
`WaitForIndexesAfterSaveChanges(5s)` *because* that lookup is index-backed. Three plain properties.

### Everything else — no

`FileCoverage` (never queried; point-loaded by content-addressed id and prefix-streamed — indexing it
would index millions of `LineCoverage` entries for nothing), `BuildTreeSummary` (point-load only),
`ApiToken` (one low-volume query; the auth hot path is a point-load on `ApiTokens/{sha256}`),
and `RavenDataProtectionKeyRepository.KeyDocument` — where `LoadStartingWith` is chosen *precisely*
so reads are ACID and index-free, and an index would be an active regression.

---

## 3. The two halves, and why only one is cheap

### Cheap half — hand-written call sites

A generated root replaces `Session.Query<T>()` and the call site reads
`context.VRepositories.Where(v => v.FullName == …).ProjectInto<VRepository>()`. `StoreAllFields`
means the projection carries every field, so most read-only sites need nothing else.

Two things to get right:

- **`ProjectInto`, not `OfType`.** The house convention is `.OfType<Commit>()` — query index entries,
  then materialize the source documents — which is why the missing-`ProjectInto` trap has never bitten
  this repo. The generated convention is the opposite. Adopting it means two conventions coexist:
  `Commits_ByRepository` keeps `OfType`, everything generated uses `ProjectInto`. That is a real,
  permanent readability cost and it should be written into the index doc-comments, not left implicit.
- **Sites that mutate must still load the document.** `RepoSettingsController` rotates `BadgeToken`;
  a projection is not a tracked entity. Index → `Id` → `LoadAsync` is the shape.

### Expensive half — the generic surface

`CoverageSparkContext` exposes four bare `Session.Query<T>()` roots, and `security.json` grants
`QueryRead` to Everyone including anonymous. Every column a user clicks in a generic grid mints or
extends an auto-index on a public surface. Pointing those at generated indexes is where the auto-index
pressure actually goes away — and it is the part that does not fit in a small change:

1. `CoverageSparkContext` must become `partial`. It is not, today. A non-partial context is
   `SPARK_INDEX_008` — a **warning**, and the roots then simply do not appear. Silent.
2. The element type changes from `Repository` to `VRepository`. `RepositoryVisibility.Filter` returns
   `Expression<Func<Repository, bool>>` and `RepositoryActions.GetRowFilterAsync` is typed to it, so
   the row filter, the parity test that pins it to `RepositoryVisibility.IsVisible`, and the custom
   query actions all move together. `.In()` must survive the move — it is load-bearing over
   `Contains` for the reasons at `RepositoryVisibility.cs:26-33`.
3. Two client-driveable grid columns have no document field behind them: `Build.Run`, and
   `Commit.Date` — which is the **declared default sort** of `Repository_Commits`. That works today
   only because `CommitActions.Repository_Commits` returns an in-memory `IQueryable`, and
   `CommitActions.cs:53-57` says so in as many words: *"if this ever returns a Raven queryable again,
   the sort must move back to an indexed field."* Moving the Commit grid onto an index is precisely
   that event. `Commit.CoverageDelta` is worse — `[JsonIgnore]`, stamped per request, in RavenDB not
   at all.
4. `App_Data/Model/*.json` and `modelHashes.json` move with it, and a production app refuses to start
   on a model mismatch.

That is a milestone, not a step, and it is bounded by the `Commit` grid rather than by the generator.

---

## 4. Sequencing

Deliberately ordered so the first step is provable on its own and the risky part is last.

1. **Bump `preview.52` → `preview.53`** across `Coverage`, `Coverage.Library`, `Coverage.Tests`.
   Nothing generated yet; this is the version that makes the attribute exist. Verify the model is
   still in sync (`--spark-verify-model`, exit 0) and the suite is green *before* anything else.
2. **`[IgnoreForIndex]` on `Build.Run`**, and a decision recorded on `Build.Sessions`. This must land
   before step 3 or the generated Build index maps a field that is not in the document.
3. **`[GenerateIndex]` on `Build` and `Account`.** Neither has a hand-written index, so no
   `SPARK_INDEX_004`. Switch `FinalizeBuildsCronJob` and `BuildActions.Commit_Builds` onto
   `Builds_Overview`, and `BrowseController.cs:192` / `MeController.cs:45` /
   `GitHubAccessService.cs:213` onto `Accounts_Overview`. Register both in the tests' index helper.
4. **`[GenerateIndex]` on `Repository`**, and move the seven `FullName`/`OwnerLogin` lookups. Largest
   measurable win; still all hand-written call sites, so still cheap.
5. **`CoverageSparkContext` partial + generic-surface cutover** — §3's expensive half, gated on
   answering SP4 for the `Commit` grid. Not scoped here.

6. **Materialize `Commit.Date` and `Commit.HasCoverage`, then generate `Commit` too** — §2's route.
   Migration + single-writer discipline, then `[GenerateIndex]` on `Commit` and
   `Commits_ByRepository` is deleted. Closes SP4 as a side effect. Independent of
   [Spark#272](https://github.com/MintPlayer/MintPlayer.Spark/issues/272): it needs no second index
   on the collection, so it does not wait on upstream.

`Commit` appears only at step 6, and never as a *second* index on the collection. Coexistence — a
generated `VCommit` alongside `Commits_ByRepository` — is possible today by suppressing
`SPARK_INDEX_004`, and is deliberately not planned: it would bind `Database.Commits` to whichever
index reflection reaches last (#272) and serve none of the six existing call sites.

## 5. Open question

**SP4 — what does the `Commit` grid sort on once it is index-backed?** `Date` is the declared default
sort and is computed; `CoverageDelta` is not persisted at all. Either the index gains a mapped
`Date` (a coalesce — so hand-written, which `Commits_ByRepository` already is and already computes as
`AuthoredAt`), or the model's default sort changes, or the grid keeps materializing in memory and
`CommitActions.Repository_Commits`'s unbounded `ToListAsync()` stays a scaling problem in its own
right. Blocks step 5 only. Cost S to answer, M to act on.

## 6. What is not verified

The generated output shape above is read from the PR's snapshot tests and the `Demo/Fleet`
conversion, not from a build of this repo. Nothing here has been compiled against `preview.53`.
Step 1 is the step that turns all of this from upstream-reading into fact.
