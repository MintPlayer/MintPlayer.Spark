# A follower waits on its leader's clock, not its own (and: upstream repositories first, mixed .NET Standard repos)

Status: **investigating** (2026-11-15). Branch `fix/follower-cooldown` (from master 15088b6e5, #672 merged + deployed).
Predecessor: [dependency-companion-pins-PRD](dependency-companion-pins-PRD.md) (`follow:`, lockstep).

## 1. The problem (observed 2026-11-15)

The first run after #672's deploy (owner pressed "Update SDK + dependencies" at 14:57; lookup 43727131844, apply
15:01 success) opened PR #673. The playwright group came out right: `Microsoft.Playwright`, `mcr.microsoft.com/playwright`
and the four `follow:` pins are one group, and lockstep picked **1.64.1** (NuGet's newest; MCR has 1.65.1). But the whole
group waits until 2026-11-18 15:01 UTC (#673 body, *Available, not applied*):

```
- `mcr.microsoft.com/playwright` 1.63.0 → 1.64.1: released less than 3 days ago (until 2026-11-18 15:01 UTC)
- `mcr.microsoft.com/playwright` v1.63.0-noble → v1.64.1: moves with its group, which waits for mcr.microsoft.com/playwright (released less than 3 days ago)
- `Microsoft.Playwright` 1.63.0 → 1.64.1: moves with its group, which waits for mcr.microsoft.com/playwright (released less than 3 days ago)
```

The first line is a **follower** (value `1.63.0`, not `v1.63.0-noble`). MCR publishes no release timestamps, so a
version's cooldown starts when the engine first sees it, keyed by (ecosystem, depName, token)
(`DependencyPolicyEngine.cs:309-319`, start at `:934`). The leader's token is `v1.64.1-noble`, first seen on 2026-11-14
(#669 moved it); the follower's token `1.64.1` is new on 2026-11-15, so it starts its own 3-day clock and lockstep holds
the group for it. Nothing is wrong with the move itself; it is three days late for no reason, and every future follower
of a timestamp-less leader is.

## 2. Owner scope (2026-11-15)

- Fix it in a new PR: branch, investigation team, this PRD with spikes, all milestones, red/green tests.
- **Upstream first.** Spark and MintPlayer.AspNetCore.SpaServices depend on MintPlayer.Dotnet.Tools, so Tools is the
  first repository to update; the others follow. The repository dependency graph is already computed on
  coverage.mintplayer.com.
- **Mixed repositories.** What happens to repositories with both .NET (Core) and .NET Standard packages? Spark is one,
  so it is the real-life test case.

## 3. Findings

### 3.1 F-C: the follower's clock (engine `CodeCoverage/Dependencies/Policy/DependencyPolicyEngine.cs`, E)

- **FC1 Token.** `Token()` (E:115-117) is the digest for a digest update, else `Version ?? Value ?? Digest`.
  `RecordFirstSeen` (E:309-331) records (ecosystem, depName, token) for every record, followers included (E:322-324);
  `Candidates()` checks `Cooldown(eco, dep, Token(c), c.ReleaseTimestamp, …)` (E:896) with `firstSeen.GetValueOrDefault(key,
  now)` (E:934). `follow.ts:80-81` sets a follower candidate's `version` and `value` both to the follower's text (`1.64.1`),
  so only the token differs from the leader's (`v1.64.1-noble`) and the follower starts its own clock. `Lockstepped`
  (E:524) drops the cooling option, and the group waits (E:548).
- **FC2 Timestamps.** `follow.ts:83` copies the leader's `releaseTimestamp`; a Docker leader has none, so neither has the
  follower. The candidate made from the leader's *current* value (catch-up, `from: null`, `follow.ts:73`) never has one:
  a lagging follower always cools 3 days toward a version its leader already runs.
- **FC3 Other per-token lookups.** Own-release (E:898) looks up `candidate.Version`, so a follower of an own package
  misses the leader's skip and holds the group (**harmful**). Fleet-red (E:882) misses too, but the leader stays flagged
  and lockstep blocks the group (harmless). The `until` date (E:472,485) and the Available rows (E:941) show the
  follower's later clock (misleading). Security and dep holds are keyed by (eco, depName), shared (harmless). Approvals
  collapse to depName@major (harmless).
- **FC4 Dead field.** `follow.ts:110` `hold` can never be true: `updateTypeOf` never returns null.
- **FC5 Fix site.** (c) the action gives a follower candidate the leader's `version` (the leader candidate's, or the
  leader's current version for catch-up) and keeps `value` as the follower's text: every token lookup (first-seen,
  own-release, red) then matches the leader in one place. (a) `Manager == "follow"` never cools fixes only the clock.
  Tests nearby: `PolicyEngineTests.Without_a_releaseTimestamp_the_cooldown_counts_from_the_first_time_the_bot_saw_it`
  (:143), `LockstepTests`, `FollowGroupingTests`.
- **FC6 (mine) Lockstep compares tokens.** `Lockstepped` reads `DefaultVersion(Token(candidate))`. A follower with a
  `version:` pattern (`1.64` from `v1.64.1-noble`) reads as `1.64`, never equal to its leader's `1.64.1`, so such a
  group could never agree. Carrying the leader's token on the follower candidate (FC5 c) fixes this too.

### 3.2 F-G: the repository graph and upstream-first (paths under `apps/CodeCoverage/CodeCoverage`)

- **FG1 The graph is computed, not stored.** `DependencyGraph.Build` (`Dependencies/DependencyGraph.cs:41-103`) builds it
  per request for `GET accounts/{provider}/{login}/dependency-graph` (`Controllers/BrowseController.cs:86-102`) from one
  `RepositoryManifest` per repository: an edge is producer → consumer with its packages; a repository produces what its
  manifest lists under `Produces`, plus the Action and ghcr image named after it (:65-72). Same account only. Manifests
  refresh after each account reconcile and nightly (`ManifestScanScheduler.cs:24-30`) and from lookup records
  (`DependencyLookupIngest.cs:8`).
- **FG2 What the updater does with it today.** A producer's release (or default-branch push) gives its consumers an
  `OwnRelease` run 30 minutes later (`Lifecycle/DependencyUpdateRecipients.cs:189-249`, matching `Produces` to
  `Consumes` itself), and the account's own new releases skip the cooldown (`OwnReleaseCheck`,
  `DependencyUpdateEngine.cs:705-713`). So "the others follow suit" already happens for package releases.
- **FG3 What's missing.** A platform move waits for its **companions** (pending until each has a release at the new
  major, `Policy/DependencyPolicyEngine.cs:697-728`; sdk-updates-PRD :89-97), but companions are only Microsoft.*,
  System.* and @types/node by name (`Policy/DependencyCompanions.cs:21-28`). The account's own packages that follow the
  platform's major (the owner's rule: a NuGet package's major is its .NET major) are not companions, so a downstream
  repository's SDK move does not wait for, or carry, the upstream repository's release on the new platform. Nothing
  orders runs across repositories (the button runs one; the cron goes in index order, `DependencyUpdatesCronJob.cs:57-91`).

### 3.3 F-S: repositories with .NET and .NET Standard projects (Spark)

- **FS1 Spark's .NET Standard projects** all target exactly `netstandard2.0`, none multi-target: SourceGenerators,
  LibraryGenerators, AllFeatures.SourceGenerators (packed), Contributions.SourceGenerators (not packed),
  CodeCoverage.Versioning.MSBuild (packed as MintPlayer.Versioning.MSBuild). No TFM property in Directory.Build.props.
- **FS2 Platform moves leave them alone.** `tfmChannel` (`dependency-action/src/platform/channels.ts:78-82`) is null
  for anything but net5+, so `nextTfmList` (`applyPlatform.ts:30-33`) skips a netstandard element; a multi-target list
  moves only the net token (`applyPlatform.ts:25-29`, test `platform.test.ts:327`); a `$(X)` resolving to netstandard
  is reported "not a netN.N TFM" (`locations.ts:829-870`, test `platform.test.ts:611`). The S19 golden fixture holds
  these csprojs.
- **FS3 Versioning leaves them alone.** `MajorAnchor.FromTargetFrameworks` (`CodeCoverage.Versioning/Versions/
  MajorAnchor.cs:24-48`) skips netstandard: such a package is free and keeps its tag's major (13.x); mixed
  `netstandard2.0;net11.0` gets 11 (`MajorAnchorTests.cs:9,17-20`).
- **FS4 Companions do NOT.** `FindCompanions` (`Policy/DependencyPolicyEngine.cs:181-205`) matches by name and major
  only, never by the project's TFM, though every record carries `packageFile`. A netstandard2.0 analyzer referencing
  `System.Text.Json` 10.x would move to 11.x with the platform; an analyzer must load in older compilers and Visual
  Studio, so that breaks consumers. Spark is not hit today (its generators reference only Microsoft.CodeAnalysis* 5.x
  and MintPlayer.* 13.0.0), and no companion test covers TFMs.

### 3.4 F-U: Spark sees none of its upstream releases (found while checking F-G on run 51)

- **FU1** In run 51's lookup (artifact `dependency-lookup-1006469943-51-1`, run 43727131844), all **139** `MintPlayer.*`
  NuGet records have **0** candidates; the other NuGet records have updates (169 of 271). Upstream releases exist on
  nuget.org: `MintPlayer.AspNetCore.Endpoints` 11.4.1-rc.1 (2026-10-24) and 11.4.1 (2026-11-12), Assertions 11.0.0
  (2026-11-15 12:20); Spark pins 11.4.1-rc.0 and 11.0.0-rc.6 (`CodeCoverage.GitWriter.csproj:26`,
  `CodeCoverage.Tests.csproj:31`). #673 doesn't name them at all: no row, no problem.
- **FU2 Suspect: `nuget.config`.** It maps `MintPlayer.*` to `github-mintplayer` (nuget.pkg.github.com/MintPlayer) and
  `*` to nuget.org; NuGet's packageSourceMapping takes the most specific pattern, so `MintPlayer.*` comes only from
  GitHub Packages, which needs credentials even for public packages. Spark's `dependency-lookup.yml` leaves
  `host-rules` commented out, and the lookup log has no 401 or feed line (Renovate runs at `LOG_LEVEL=info`,
  `renovate.ts:86`). The run's only problem is "Custom registries are not allowed for this datasource and will be
  ignored". Spike S4 confirms or refutes.
- **FU3 Consequence for upstream-first.** Even with F-G's companions, Spark could never ride or wait for an upstream
  release it can't see; and the owner's "packages won't have published yet, unless you use nuget.pkg.github.com"
  (2026-11-15) says GitHub Packages is where MintPlayer releases appear first.

## 4. Goals

- G1 A follower never waits longer than its leader (FC1-FC3, FC6).
- G2 A downstream repository sees its upstream repositories' releases (F-U), and its platform move carries them, or
  waits for them (F-G): Tools first, the others follow.
- G3 A platform move leaves a .NET Standard project's references alone (FS4); TFMs and versioning already do (FS2, FS3).
- G4 A feed the lookup can't read is a "not moved"/problem line in the body, never silence (FU1).

## 5. Spikes

| # | Question | How | Decides |
|---|---|---|---|
| S1 | The leader's token for MCR: `version` or `value`? | Real report `test/fixtures/renovate/custom-managers/report.json` | M1 |
| S2 | Can the engine see a project's TFM? | `feeds.ts:81-101`, `locations.ts:104-136` | M3 |
| S3 | Which upstream packages Spark consumes, and their majors | csprojs, nuget.org | M4 |
| S4 | Does the `MintPlayer.*` → GitHub Packages mapping hide updates, and does a host rule fix it? | Pinned Renovate image on two fixture repos (Spark's nuget.config vs nuget.org only) | M2 |

### 5.1 Spike results

- **S1 (done):** Renovate reports `newVersion: v1.64.1`, `newValue: v1.64.1-noble`; the engine's token is `Version` first
  (E:115-117), so the leader's token is `v1.64.1` (that is why #673 prints "v1.63.0-noble → v1.64.1"). The follower
  candidate must carry the leader candidate's `version ?? value`; for catch-up, the leader's
  `currentVersion ?? currentValue`.
- **S2 (done):** yes. `buildPlatformRecord` keeps every found location (`locations: found.map(toLocation)`,
  `feeds.ts:99`), and `scanTfm` emits a `tfm` location for every `<TargetFramework(s)>` element, with `channel: null` when
  no token is net5+ (`locations.ts:115-123`). So a record whose `packageFile` has `tfm` locations, none with a channel,
  is a .NET Standard/Framework-only project. A record whose file has no `tfm` location (Directory.Packages.props,
  Directory.Build.props) can't be told and stays a companion.
- **S3 (done):** real (non-fixture) references: `MintPlayer.AspNetCore.Endpoints` 11.4.1-rc.0,
  `MintPlayer.Assertions` 11.0.0-rc.6, `MintPlayer.AspNetCore.SpaServices`(+`.Xsrf`, `.Prerendering`) 11.0.0-rc.3,
  `MintPlayer.AspNetCore.OpenSearch` 11.0.1-rc.1, `MintPlayer.SourceGenerators.TestHarness` 11.0.0-rc.1: on the
  platform's major 11, so companions under G2. `MintPlayer.SourceGenerators*`, `ValueComparerGenerator*` 13.0.0 are
  .NET Standard (free major, FS3): not companions, as wanted. #673 applies the SDK's move to 11.0.100 (rc.2 → GA) with
  the Microsoft companions only (body rows 41-49).
- **S4 (done, confirmed):** pinned Renovate 44.125.2, `--dry-run=lookup`, `LOG_LEVEL=debug`, on two one-csproj repos
  (`MintPlayer.AspNetCore.Endpoints` 11.4.1-rc.0 + `Octokit` 13.0.0):
  - (a) Spark's `nuget.config`: Endpoints gets `registryUrls: [nuget.pkg.github.com/MintPlayer]` only; the log says
    "hostRules: no authentication for nuget.pkg.github.com", then `GET …/index.json` **401**; the report has no update
    and a per-dependency warning "Failed to look up nuget package MintPlayer.AspNetCore.Endpoints: no-result".
    Octokit (nuget.org) gets 13.0.1 and 14.0.0.
  - (b) nuget.org only: Endpoints gets **11.4.1**.
  - (c) Spark's `nuget.config` plus a packageRule `registryUrls: [nuget.org, nuget.pkg.github.com/MintPlayer]`:
    Endpoints gets **11.4.1** despite the 401 (the rule overrides the mapping).
  - So the mapping hides every `MintPlayer.*` update, and the per-dependency `warnings` that say so are dropped by
    `mapReport` (FU1: the body is silent).
  - Spark's own CI already reads that feed with the job's token: `dependency-apply.yml:39` (`packages: read`) and
    `:70-71` (`GH_PKG_USER: github.actor`, `GH_PKG_TOKEN: secrets.GITHUB_TOKEN`), `pull-request.yml:22-28`. So the
    lookup can read it the same way.

## 6. Plan

- [x] M0 Investigation (3 agents) + spikes S1-S3, findings in §3.
- [x] **M1 Follower clock (G1).** The action gives a follower candidate the leader's token as `version` (S1) and drops
  the dead `hold` (FC4); the engine never cools a `follow` candidate whose token is a leader record's current token
  (catch-up, FC2). Done:
  - `follow.ts` `record()`: `version` = leader candidate `version ?? value`, catch-up = leader
    `currentVersion ?? currentValue`; `updateType` is always the follower's own (never null), so `hold: false`.
    Red→green: `follow.test.ts` "gives a follower candidate its leader candidate's token…" and "…catch-up…" (2 failed →
    96/96 lookup tests pass).
  - Engine `CatchesUp` (`DependencyPolicyEngine.cs`, beside `Cooldown`). Red→green:
    `FollowGroupingTests.A_follower_catching_up_to_its_leaders_current_release_does_not_cool` (0 planned → planned).
  - Guards (green at once, since the engine already keys on the token): `A_follower_shares_its_leaders_first_seen_clock`
    (leader first seen 4 days ago, follower never seen → moves); `LockstepTests.A_follower_written_without_a_patch_still_agrees_with_its_leader`
    (FC6). FC3 (own release) follows from the same token: `own` is keyed on `candidate.Version`; no separate test.
- [x] **M2 Feeds (G4, FU).** Done:
  - `mapReport.ts` `reportProblems` turns each dependency's Renovate `warnings` into a problem with source
    `LOOKUP_WARNING` (`renovate-lookup`, the server's `LookupProblem.LookupWarning`). Test on the real S4 (a) report,
    committed as `test/fixtures/renovate/private-feed/report.json`. (Written before its test; it was not seen red.)
  - `renovate.ts` `hostRules(token, user, actor)`: the user's rules plus a `nuget.pkg.github.com` rule with the job
    token, unless a user rule names that host; unreadable user rules pass through unchanged. `dockerArgs` takes
    `hostRules: boolean`; the value travels by name only. Red→green: 6 tests in `renovate.test.ts`
    ("hostRules is not a function" → 498/498 action tests).
  - The lookup template and Spark's `.github/workflows/dependency-lookup.yml` (identical bytes) grant `packages: read`.
    Red→green: `templates.test.ts` "has exactly read permissions…". `DependencyWorkflowGuard` refuses only write
    access and `id-token`, so the bot keeps dispatching it.
  - Engine `NotLookedUp` → `DependencyRunReport.NotLookedUp` → body section "## Not looked up" naming
    `packages: read` / `host-rules`. Test: `A_dependency_the_lookup_could_not_read_is_listed_under_not_looked_up`
    (red as a compile failure, then green).
  - Limit: an onboarded repository keeps its old lookup workflow (the workflow-outdated pull request fires only on a
    missing input, `DependencyUpdateEngine.cs:383-393`). There the token has no `packages` scope, the lookup still
    gets a 401, and the body now says so with the fix. Spark's copy changes in this PR.
  - Original plan text: Per S4. A record whose lookup failed on a feed is a problem the body names; the lookup can
  read GitHub Packages for the account's own packages. Design (E3): the action adds a `nuget.pkg.github.com` host rule
  built from the `github-token` input (unless the user's `host-rules` already name that host), and the lookup template
  grants `packages: read`, as Spark's CI does. No nuget.org fallback via `registryUrls` (variant c): the mapping
  exists to stop dependency confusion, and widening it would undo that. The template change reaches onboarded
  repositories through the workflow-outdated pull request; until it lands, the warning says why.
- [x] **M3 .NET Standard companions (G3).** `FindCompanions` skips a record whose `packageFile` has `tfm` locations and
  none with a channel (S2). Done: red→green
  `PlatformInChannelMoveTests.A_dotnet_standard_projects_reference_neither_rides_nor_holds_the_platform_move` (a
  netstandard2.0 csproj's `System.Text.Json` rc.2 with only rc.3 on offer held the RC→GA move; now the move plans and
  `System.Text.Json` is its own group). Spark's real generators reference no package on major 11 today
  (`MintPlayer.SourceGenerators.Tools` 10.21.0, `Microsoft.CodeAnalysis*` 5.x), so the fixture is synthetic.
- [x] **M4 Upstream packages are companions (G2).** A package another repository of the account produces
  (`RepositoryManifest.Produces`), on the platform's major, rides the platform move like a Microsoft one, and the move
  waits for its release. `companions: []` still turns all of it off. Done:
  - `DependencyPolicyConfig.AccountPackages` (+ `AccountPackageKey`, `IsAccountPackage`, `Resolve(..., accountPackages)`);
    `FindCompanions` matches a glob **or** an account package.
  - `DependencyUpdateEngine.AccountPackagesAsync` (plan time): `Repositories/Overview` by `OwnerKey` (Take 1024, as
    `DependencyPropagation` does), then one batch `LoadAsync` of the manifests. 2 requests, no per-item load (session cap).
    The repository itself is left out.
  - Red→green (`PlatformInChannelMoveTests`): `An_upstream_package_of_the_account_rides_to_its_release` (Endpoints
    11.4.1-rc.0 → 11.4.1 in `platform:dotnet 11.0`), `An_upstream_package_without_its_release_holds_the_move_until_forced`
    (SpaServices rc.3 with only rc.6 → Pending). Guard: `Companions_off_leaves_the_upstream_packages_alone_too`.
  - End to end: `DependencyPreviewToReleaseEndToEndTests.An_upstream_package_of_the_owner_rides_with_the_platform_move`
    (sibling repository's manifest → plan item). `DependencyHarness.SeedAsync` now deploys `Repositories/Overview`:
    without it every saga test failed with `IndexDoesNotExistException` (48 + 119 harness tests green after).
  - Consequence (by design, the owner's upstream-first order): Spark's RC→GA move waits until SpaServices ships 11.0.0;
    a writer can force it with `/approve platform:dotnet 11.0 --force`.
- [x] **M5 Docs:** `dependency-updates.md` (§2 GitHub Packages + "Not looked up"; §4 upstream companions, .NET
  Standard, follower clock), sdk-updates-PRD D2 amended, companion-pins-PRD M2 superseded-in-part mark, setup page
  wording (`packages: read`), this PRD.
- [ ] **M6 Gate:** PR, CI green, coverage via the MCP. Status 2026-11-15: M1-M5 done and uncommitted on
  `fix/follower-cooldown`; `dist` rebuilt in the main checkout; targeted local runs green (action 498/498 + templates
  36/36; C# Follow/Lockstep 14, PlatformInChannel 12, saga + E2E 48, other harness classes 119, onboarding/lifecycle
  102). Awaiting the owner's word to commit, push and open the PR.

## 7. Decision log

| # | Decision / evidence | Source |
|---|---|---|
| E1 | The follower's own first-seen clock held #673's playwright group 3 days. | PR #673 body; `DependencyPolicyEngine.cs:316,934` |
| E2 | A follower candidate carries the leader's token as `version`, so it shares the leader's first-seen clock. | S1 |
| E3 | GitHub Packages is read with the job's token (`packages: read`), never bypassed with a nuget.org fallback. | S4 (a)-(c); `dependency-apply.yml:39,70-71` |
