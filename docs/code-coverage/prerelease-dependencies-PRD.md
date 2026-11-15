# Prerelease dependencies, stable packages and platform approvals (MintPlayer.Dotnet.Tools)

Status: **merged** in PR #671 (2026-11-15; M1-M9). Owner decided Q1-Q3 on 2026-11-15 (§6, decision log). This PRD is also the
plan (§8-§10). It continues `versioning-beta-upstream-PRD.md` (whose M4 is still open) and refines decisions in
`platform-locations-PRD.md` (P11) and `dependency-updates-PRD.md` (D-P18, #606 D8). Where this text proposes changing
one of those decisions, it says so and waits for the owner.

## 1. The questions (owner, 2026-11-15)

1. `GraphQL/MintPlayer.GraphQL.Tools` (PackageId `MintPlayer.GraphQL`, `net10.0;net11.0`) depends on `Octokit.GraphQL`
   `0.4.0-beta` on purpose. The library should ship **stable** and still **accept preview updates** of Octokit.GraphQL.
   Does that work?
2. Why does nothing move from .NET 10 to .NET 11 without an explicit approval?
3. Every scenario deserves a dedicated unit or e2e test, including "a package that is still in preview, used by
   libraries that ship stable".

## 2. Short answers

| # | Answer | Evidence |
|---|---|---|
| 1a | **Shipping stable: designed and built, not yet switched on in Tools.** Without a pin the planner labels GraphQL `beta` (Octokit), `11.0.0-beta.1` sorts below the open `11.0.0-rc.1`, the plan is refused, and the all-or-nothing run blocks **all 40 packages'** .NET 11 GA releases. The `stable` pin (G2) shipped in #666 and is deployed; Tools' `coverage.yml` doesn't set it yet. | §3.1 |
| 1b | **Accepting Octokit previews: not reliable, and silent when it fails.** The engine never applies a package's track (`PackageTrack` has no caller). Whether a newer prerelease is offered is left to Renovate's default `ignoreUnstable`, and nothing is reported when one is skipped. Today it's harmless only because `0.4.0-beta` is Octokit.GraphQL's newest version. | §3.2 |
| 2a | **.NET 10 → 11 (a major): a documented decision.** D-P18/R7: "A platform major never auto-merges, even when the file admits it." `allowMajor: true` plans the move without an approval but still doesn't auto-merge it. | §3.3 |
| 2b | **11.0 rc → 11.0.100 GA (in-channel) waits too, because of P11.** Two conditioned TFMs (`net11.0`) in the benchmark csproj count as "same-channel locations the bot cannot edit". P11 is an owner decision (2026-11-13), but it didn't consider a location that is already correct and that the move would never change. The PR body hides the reason. | §3.4 |

## 3. Findings (evidence)

Paths: `CC` = `apps/CodeCoverage/CodeCoverage`, `CV` = `apps/CodeCoverage/CodeCoverage.Versioning`, `DA` =
`apps/CodeCoverage/dependency-action/src`. The investigation was code reading plus GitHub/nuget queries. Nothing was run;
where a claim is inferred, the text says so.

### 3.1 Versioning: the package's own version

- `ReleasePlanner.ReleaseLabel` (`CV/Releases/ReleasePlanner.cs:411-419`) applies, in order: package pin, line pin, repo
  pin, `UpstreamLabel`. A `stable` pin returns no label (`:418`, versioning-beta-upstream G2, #666 `e5a3ab446`).
- `UpstreamLabel` (`:427-454`) takes the least mature label of three inputs:
  - the build's SDK, only when it has the same major as the TFM-anchored major (`:431-434`);
  - in-repo dependencies, at the version this run gives them or their last tag (`:437-445`);
  - declared dependencies as restore resolved them (`:446-452`).
  It doesn't read transitive dependencies.
- MintPlayer.GraphQL with no pin and a GA SDK gets `beta` (Octokit), so `11.0.0-beta.1`. `VersionCalculator.Next`
  (`CV/Versions/VersionCalculator.cs:56-74`) refuses it because it isn't above the existing `11.0.0-rc.1`. The run is all
  or nothing (`ReleaseRun.cs:96-98`, G1).
  - Proof: run 43509498988 (2026-11-13), "40 tags … not written".
  - It is still the last `publish-release` run on Tools' master (checked 2026-11-15).
- `v11.0.0-rc.1` exists because the SDK the release ran on was `11.0.100-rc.1` (every workflow pins it, e.g.
  `dotnet-build-master.yml:37`). This is inferred from `:431-434` plus `rc` being more mature than `beta`.
- Propagation:
  - An in-repo dependent mirrors the dependency's own released version (`:437-445`). So a beta GraphQL would make its
    dependents beta.
  - A stable-pinned GraphQL makes them stable; Octokit's label doesn't pass through. This is inferred and not tested.
- NU5104 (a stable package with a prerelease dependency) is a NuGet **warning**. Tools has no `TreatWarningsAsErrors`; its
  `Directory.Build.props` only promotes MPV006. GraphQL shipped 8.0.0-10.0.0 stable with the same beta.

### 3.2 Dependency updates: the preview package itself

- `DependencyPolicyConfig.PackageTrack` (`CC/Dependencies/Policy/DependencyPolicyConfig.cs:127`) has **no caller** (grep,
  2026-11-15).
  - Its sibling `UnstableGlobs()` is documented (`:141-143`) as "a superset is harmless: the engine still screens each
    candidate against PackageTrack, and an enforced rule narrows it there".
  - **That comment is false**: `DependencyPolicyEngine.Candidates` (`CC/Dependencies/Policy/DependencyPolicyEngine.cs:765-806`)
    has no track or prerelease check.
  - Consequence: an enforced account rule `track: stable` doesn't narrow a file's `track: preview` for packages. That is
    a defect (D2).
- The only gate is Renovate's `ignoreUnstable`:
  - `DA/lookup/renovate.ts:29-31` sets `ignoreUnstable: false` for `UnstableGlobs()` only. That covers `track: preview`
    rules and the companions of a preview-track SDK.
  - `DependencyUpdateEngine.cs:328` passes them.
  - Every other package gets Renovate's default.
- What Renovate's default does when the current version is already a prerelease is **unverified** (spike SP1). My
  recollection is that Renovate offers another prerelease only when it has the same `major.minor.patch` as the current
  one. If so, `0.4.0-beta → 0.5.0-beta` would be dropped before the engine sees it.
- Packages have no counterpart of D-P18's "or any when the repository already is on one", which platforms get through
  `Follows()` (`DependencyPolicyEngine.cs:569-572`).
- **Silent gap (D3):** a skipped prerelease leaves no line in the PR body. Platforms report `PreviewNotFollowed`
  ("set platforms.X.track: preview to follow it", `:578-581`); packages report nothing.
- Docs: `docs/code-coverage/dependency-updates.md:196` mentions `track: stable | preview` for package rules in one line,
  with no example. It doesn't say how a package already on a prerelease behaves.
- Reality check: nuget lists Octokit.GraphQL `0.1.0-beta` … `0.4.0-beta`, never stable. Tools pins `0.4.0-beta`
  (`MintPlayer.GraphQL.Tools.csproj:28`), and PRs #208 and #210 mention no Octokit. So the output was correct, but only by
  luck.

### 3.3 Platform majors (.NET 10 → 11)

- D-P18 (`dependency-updates-PRD.md:1230-1234`): `platform:*` defaults to minor, so a major waits for
  `/approve platform:<sdk> <channel>`. The code is at `DependencyPolicyEngine.cs:661-669`; the comment at `:703-704`
  reads "R7: a platform major never auto-merges, whatever the file says".
- Config:
  - `allowMajor: true` makes the inclusion major (`DependencyPolicyConfig.cs:97-101`), so the move is planned without
    an approval.
  - It is still not auto-merged (`:704`).
  - `autoMerge` can't lift it.
  - An enforced account `maxUpdate` is a ceiling that even an approval can't exceed (`:653-657`).
- Other blockers of a major:
  - ignore (`:596`), hold (`:601`)
  - fleet red (`:607`)
  - pending companions, which `--force` overrides (`:618-650`)
  - preview track (`:578`)
  - cooldown (`:692`)
  - the ceiling
- Multi-target TFMs follow `platforms.dotnet.strategy` (`DA/apply/applyPlatform.ts:34,41`, default `replace`):
  - `replace` changes the from-channel token;
  - `add` + `keepPrevious` appends (`platform.test.ts:222-228`);
  - a list whose tokens are all older than the target is reported, never edited (P6, `applyPlatform.ts:252-255`).
  
  What "current channel" is for Tools, where libraries target `net10.0;net11.0` and the SDK is 11.0 rc, isn't proven
  (spike SP4).

### 3.4 In-channel move (rc → GA) held by P11

- An in-channel move is a Patch (`DependencyPolicyEngine.cs:594`), and the default inclusion is minor. So the gate at
  `:661` doesn't fire.
- P11 does (`:671-690`). It fires for locations with `Computed && Channel == current`, in scope and not digest-pinned.
  `locations.ts:112-124` marks a `Condition` TFM computed, reason "conditioned TFM (Condition attribute)", with the
  channel of its literal token (`net11.0` → `11.0`). PR #210 listed exactly the two TFMs in
  `MintPlayer.SourceGenerators.Tools.Benchmarks.csproj` under "Not moved".
- P11 is an owner decision (`platform-locations-PRD.md:564`, 2026-11-13): "This changes in-channel moves (rc → GA,
  patch), which otherwise merge through the queue unseen". The motive is that a half-done move shouldn't merge unread.
  But an in-channel move never changes a TFM: `net11.0` is already right for 11.0.100. Holding it is a false positive of
  that rule.
- **D1, display defect:**
  - `DependencyPullRequestBody.cs:64-69` prints one fixed header, "Over the auto-merge threshold
    (`dependencies.autoMerge` in coverage.yml)".
  - Each row has Group/Dependency/From/To/Comment; `entry.Detail` (the real reason, `:684-686`) is never shown.
  - The header is wrong for P11 holds, and only partly right for majors (R7 is not the threshold).
- PR #210 was **closed by the owner's account** (issue events, 2026-11-14 18:40:57Z), not by the bot. The
  `mintplayer/dependencies` branch still exists, and no newer dependency PR is open.

## 4. Defects (independent of the owner questions)

| # | Defect | Fix |
|---|---|---|
| D1 | "Awaiting approval" hides why a group waits; the header claims the threshold for every row. | A Reason column fed by `entry.Detail`, plus a neutral header. |
| D2 | `PackageTrack` is never applied, so an enforced `track: stable` can't narrow a package, contrary to the `UnstableGlobs` doc comment. | The engine screens package candidates against `PackageTrack`. |
| D3 | A prerelease skipped because of the track leaves no report line for packages, unlike platforms. | A `PreviewNotFollowed` line for packages naming the rule to add. Feasibility depends on SP2. |
| D4 | The versioning refusal "upstream is less mature than the open prerelease line" doesn't name the fix. | Append "pin it: `versioning.packages.<id>.prerelease: stable` (or a label)". |
| D5 | (SP3) `ResolvedVersions` keeps an arbitrary version when a package resolves differently per TFM. | Keep the least mature version per id. |
| D6 | (SP6) R16 never sees the workflow files a platform move edits. | Q5 → H6: by design, not a defect. The R16 hole stays open on purpose; the missing piece is D7. |
| D7 | (H6) A `setup-*` input given as `${{ env.X }}`, `${{ inputs.x }}` or `${{ vars.X }}` is a computed location with no channel (`locations.ts:462-463`). P11 skips it (`l.Channel == current`), so the move edits the literal pins and leaves that workflow on the old SDK. | Resolve `env.X` and an input's literal `default:` in the same file into a `workflow-value` location that moves with the rest. A value the bot can't resolve (`vars.*`, other expressions) holds the move like P11. |
| D8 | (M6) An account rule on `platform:<sdk>` matched its glob against `<sdk>` for the track (`PlatformTrack`) but against `platform:<sdk>` for the ceiling, threshold and inclusion (`DependencyPolicyEngine.cs` `config.Ceiling(eco, eco)`). So `{ecosystem: platform:dotnet, match: dotnet, maxUpdate: minor, enforced}` set the track and never capped a major. Red: `C10_an_enforced_account_rule_still_wins_over_the_opt_in("dotnet")` planned the 12.0 move. | `AccountMatches` tries both spellings for a `platform:` ecosystem, so either match answers both questions; no existing rule loses a match. |

### 4.1 Defects found by Tools' first GA release (run 43711348322)

| # | Defect | Fix |
|---|---|---|
| D9 | The versioning action fetched the tags after tagging only in server mode (`main.ts:339`). The app pushed the tags and then failed. The local fallback found them on the remote (`already-present`) and created none in the checkout, so `dotnet pack` read the previous rc tags. The check step then refused every package, and nothing was published. | `finish()` fetches the tags in both modes. Red → green: `main.test.ts` "fetches the tags after a local fallback…" (expected false to be true). c4b6c1412. |
| D10 | `UpstreamReleases.RecordAsync` loaded one `PackageReleaseRecord` per released package. For 40 packages, the session hit RavenDB's 30-request cap after the tags were pushed, and the app answered 500, which triggered D9. | One batched `LoadAsync<T>(ids)`. Red → green: `UpstreamReleasesTests.A_run_that_releases_many_packages_records_them_in_one_request` (production exception; `NumberOfRequests == 1`). 469571934. |
| D11 | `/release` (`ReleaseRecipient`) recorded each promoted tag with its own load. A line promotes every package at once, so Tools would hit the same cap after the writer pushed. | One batched load of the promoted tags' records before the loop. Red → green: `ReleaseFlowTests.A_release_that_places_many_tags_records_every_one` (production exception). 85d653bbd. |

## 5. Scenarios

Each row gets its own test. "Exp." is the proposed behaviour; where it depends on an owner question, the question is
named. Test kinds:

- **U** = C# unit in `CodeCoverage.Tests/Dependencies/Policy` (`DependencyTestKit`)
- **V** = `CodeCoverage.Versioning.Tests`
- **T** = vitest in `dependency-action`
- **I** = dependency-action integration (real Renovate/registries, `src/integration/*.integration.test.ts`, run in CI's
  `DEP_ACTION_IT_GROUP` slices)
- **P** = `RealPackTests` (real `dotnet pack`)

### 5.1 A preview package (dependency updates)

| # | Scenario | Today | Exp. | Test |
|---|---|---|---|---|
| A1 | Default track, current `0.4.0-beta`, newer `0.5.0-beta` exists | unverified (SP1), probably dropped silently | Q1 | U + I (real fixture: pin `Octokit.GraphQL 0.3.0-beta`, a newer core `0.4.0-beta` exists) |
| A2 | Default track, current `1.0.0-beta.1`, newer `1.0.0-beta.2` (same core) | Renovate probably offers it (SP1) | offered as a patch-level update | U + I |
| A3 | Current prerelease, first stable appears (`0.4.0-beta → 1.0.0`) | offered; `0.x → 1` is a major (R17) | offered, approval as any major | U |
| A4 | A file rule `match: [Octokit.GraphQL]`, `track: preview` | Renovate offers prereleases; the engine doesn't screen | offered | U + T (`renovateConfig` emits the `packageRules` entry) |
| A5 | A4 plus an **enforced** account rule `track: stable` on the same package | prereleases pass (D2) | not offered, reported | U |
| A6 | Current stable, default track, prerelease newer than latest stable | not offered, no line | not offered; line per Q1/D3 | U |
| A7 | A prerelease skipped by the track | no line (D3) | "Available, not applied" line naming the rule | U + body test |
| A8 | Two projects reference Octokit.GraphQL at the same prerelease | one group | one group, both files | U |
| A9 | A preview-track SDK companion that is also a direct package | companion rules (#606 D8) | unchanged; regression guard | U (exists: `PlatformTrackCompanionTests`) |

### 5.2 A stable library over a preview package (versioning)

| # | Scenario | Today | Exp. | Test |
|---|---|---|---|---|
| B1 | `stable` pin, `beta` runtime dependency, open `11.0.0-rc.1` line | `11.0.0` | `11.0.0` | V (exists, versioning-beta-upstream M1) |
| B2 | No pin, `beta` dependency, GA SDK, open rc line | refused | refused, message names the pin (D4) | V |
| B3 | In-repo dependent of a stable-pinned package that has a beta dependency | stable (inferred) | stable | V |
| B4 | Multi-TFM `net10.0;net11.0` with a conditional `PackageReference` per TFM resolving different versions | unknown (SP3) | least mature over all TFMs, or as SP3 decides | V |
| B5 | SDK goes rc → GA mid-line, no other prerelease: open `11.0.0-rc.1` | `11.0.0` (inferred) | `11.0.0` | V |
| B6 | `stable` pin while a later `11.0.0-rc.3` tag exists | untested | `11.0.0` (above every rc) | V |
| B7 | Prerelease only transitive (declared dependency stable) | stable (declared only, `:446`) | stable; pins today's documented behaviour | V |
| B8 | Real pack of a stable package with a prerelease dependency | NU5104 warning (inferred) | pack succeeds with the warning; fails only under `TreatWarningsAsErrors` | P |
| B9 | The Tools fix end to end: `coverage.yml` with the GraphQL stable pin, GA SDK | refused today | all 40 tag `11.0.0`/GA | V (plan-level, Tools-shaped fixture) |

### 5.3 Platform moves

| # | Scenario | Today | Exp. | Test |
|---|---|---|---|---|
| C1 | rc → GA, no `global.json`, only `setup-dotnet` pins + literal TFMs | patch, auto-merge (inferred) | same | U |
| C2 | C1 + a conditioned TFM `net11.0` on the current channel | **held (P11)** | Q2 | U (red first if Q2 = A) |
| C3 | C1 + a `$(Prop)` TFM without a known channel | doesn't hold | same | U (exists, PRD:322) |
| C4 | C1 + a conditioned TFM on the channel that **would** change (`net11.0` on an 11 → 12 move) | held | held (P11 as decided) | U |
| C5 | 10 → 11 major, defaults | awaiting approval | Q3 | U (exists: `ApprovalPolicyTests`/`PlatformRecordPolicyTests`; add if missing) |
| C6 | 10 → 11 with `allowMajor: true` | planned, not auto-merged | Q3 | U |
| C7 | 10 → 11 on `net10.0;net11.0` libraries + SDK pin 10.0 (`replace`, `add`, `add`+`keepPrevious`) | unknown (SP4) | as SP4 documents | T + U |
| C8 | Approval reason visible in the PR body (threshold, R7 major, P11, pending) | hidden (D1) | Reason column | body test (`DependencyLifecycleRulesTests` holds today's body tests) |
| C10 | 10 → 11 with the H3 opt-in `platforms.dotnet.autoMerge: major`; then the same with an enforced account `maxUpdate: minor` | n/a | auto-merged; refused by the ceiling | U |
| C11 | A platform move that edits a workflow no `pull_request` run tests (R16), with and without the opt-in | not refused (SP6: R16 doesn't see it) | not refused (H6): covering the other workflows is the repository's job | U (enqueue gate: a platform-only report with workflow edits passes) |
| C12 | `dotnet-version: ${{ env.DOTNET }}` with `env: DOTNET: 10.0.x` (workflow or job level), 10 → 11 | the env value stays on 10 (D7) | the env value moves to `11.0.x` with the rest | T (scan + apply) |
| C13 | Composite `action.yml`: `dotnet-version: ${{ inputs.dotnet-version }}`, input `default: '10.0.x'` / `default: ''` | the default stays (D7) | a literal default moves; an empty default is the caller's (no location, no hold) | T |
| C14 | `dotnet-version: ${{ vars.DOTNET }}` on a move | not held | held like P11: the location's reason names the expression; `/approve` or `exclude` | T (reason) + U (hold) |
| C9 | Approval of an in-channel move binds the exact version | bound (#617, `PlatformInChannelMoveTests:127`) | same | exists |

## 6. Owner questions

**Answered 2026-11-15** (owner, question dialog): Q1 → **A**, Q2 → **A**, Q3 → **B** (against the recommendation),
M0 → file now. Q4 wasn't asked: it is handled as defect D2 because the code already documents the behaviour. See H1-H4
in §11. The options are kept below for the record. Q5 (R16 and platform moves, raised by SP6) was answered the same
day in chat: H6/H7 in §11.

**Q1. A package already on a prerelease: follow its prereleases without a rule?**
- **A (recommended):** yes, like D-P18 does for platforms ("or any when the repository already is on one"). A package
  whose current version is a prerelease is offered newer prereleases on the default track; `track: stable` on a rule
  turns that off. Octokit.GraphQL then needs no config.
- **B:** no, it needs a `track: preview` rule; the skipped prerelease gets a report line (D3) naming that rule.
- **C:** no change beyond D2/D3.

A changes the default behaviour for every onboarded repo that sits on a prerelease. B is explicit but needs a line per
repo.

**Q2. P11 and a location the move doesn't change.**
- **A (recommended):** a computed location whose value already names the target channel holds nothing. For an
  in-channel move that's every TFM, so the move auto-merges. P11 still holds for anything that would stay behind
  (C4).
- **B:** keep P11 as decided. The repo lists the file under `platforms.dotnet.exclude`, or a writer approves.

This refines the owner's P11 (2026-11-13).

**Q3. D-P18: may a repository opt in to auto-merging a platform major?** The question was "why won't it move 10 → 11
without my approval".
- **A (recommended):** keep D-P18. A major waits for `/approve`, and `allowMajor` stays "planned, not auto-merged".
  Reasons: a platform major changes TFMs, package majors (our versions follow the TFM) and the release line, so the step
  needs a person.
- **B:** add an explicit opt-in (`platforms.dotnet.autoMerge: major`). This reverses D-P18/R7 for repositories that ask
  for it.

**Q4. Should the engine screen package candidates against `PackageTrack` (D2)?** This is treated as a defect fix, since
the code already documents that it does. Asked only because it changes behaviour for repos with enforced account rules.
- **A:** yes (recommended).
- **B:** no; delete the comment instead.

## 7. Spikes

Each spike records its evidence (command, output excerpt, file:line) under §11 before the milestone that depends on it.

- **SP1 Renovate's prerelease rule for NuGet.**
  - Question: with the default `ignoreUnstable` and current `0.3.0-beta`, does Renovate offer `0.4.0-beta`? With
    current `1.0.0-beta.1`, does it offer `1.0.0-beta.2`?
  - Method: the integration harness (`node-dotnet.integration.test.ts` setup) on a real fixture referencing
    `Octokit.GraphQL 0.3.0-beta`, then the pinned Renovate version's `lookup/filter` source.
  - Output: the rule, with a link to the Renovate source at the pinned version.
  - Decides how much of A1/A2 is ours.
- **SP2 What Renovate reports about skipped prereleases.**
  - With `ignoreUnstable: true`, does the JSON report carry the prerelease at all (so D3 needs no second lookup)?
  - With `ignoreUnstable: false` for everything, does a prerelease displace the stable candidate in the per-update-type
    results?
  - Decides the design for Q1 = A: either "look up everything unstable, the engine filters" (what the `UnstableGlobs`
    comment assumes), or "pass currently-prerelease packages to `packageRules`".
- **SP3 Multi-TFM resolved versions.** `BuildManifest.cs:118-146` reads `project.assets.json`. With a per-TFM conditional
  `PackageReference`, which target's version lands in `ResolvedVersions`? Decides B4.
- **SP4 Current channel and `replace` on mixed TFMs.**
  - For a repo with SDK pin 10.0, libraries `net10.0;net11.0` and a 10 → 11 move: what is `platform.Current.Channels`
    (`locations.ts`/`foldPlatform.ts`)?
  - What does `replace` write: `net11.0;net11.0`, de-duplicated, or untouched?
  - Same question for Tools today (SDK 11.0 rc).
  - Decides C7 and whether a duplicate-TFM bug exists.
- **SP5 NU5104 under a real pack.** `RealPackTests` harness: a stable package referencing a prerelease. Is the warning
  emitted, does the pack succeed, and what happens with `TreatWarningsAsErrors`? Decides B8's assertion.

### 7.1 Spike results (2026-11-15)

**SP1 (done): Renovate 44.125.2 drops `0.3.0-beta → 0.4.0-beta`.**
- The source read was `lib/workers/repository/process/lookup/filter.ts` at tag `44.125.2`, lines 166-193, fetched
  through `gh api repos/renovatebot/renovate/contents/...?ref=44.125.2`.
- `ignoreUnstable` defaults to `true` (`lib/config/options/index.ts:1919-1924`).
- When the current release is stable, only stable releases pass (`:170-172`).
- When it is unstable, a stable release always passes, and an unstable one passes only with the same major (`:181`).
  Unless the versioning sets `allowUnstableMajorUpgrades` (`:185`), it also needs the same minor and patch (`:189-192`).
- Only `lib/modules/versioning/node` sets `allowUnstableMajorUpgrades` (GitHub code search; the npm, semver, docker,
  pep440 and maven sources have no match). NuGet doesn't.

What that means for packages on a prerelease today:
- `0.3.0-beta → 0.4.0-beta`: dropped (minor differs). Scenario A1 is a real silent gap.
- `1.0.0-beta.1 → 1.0.0-beta.2`: offered (A2).
- `0.4.0-beta → 1.0.0`: offered (A3).

**SP2 (done): Renovate's report can't show what it skipped, and "unstable for everything" would displace stable
candidates.**
- `mapReport.ts:150-185` reads only `updates`, which Renovate builds after `filterVersions`
  (`lookup/index.ts:394-427`). Skipped prereleases never reach us.
- Renovate keeps one release per bucket: the highest after sorting (`lookup/index.ts:427-446`).
- Buckets are keyed only by numbers: `vN`, `vN.M`, `patch` (`bucket.ts:38-82`, with our `separate*` options).
- With `ignoreUnstable: false`, `5.12.2-beta` would win the patch bucket over `5.12.1`, and `6.1.0-beta` the `v6`
  bucket over `6.0.0`.
- So the `UnstableGlobs` comment's premise ("a superset is harmless") doesn't hold for Renovate's buckets.

**Design (H5):**
- Package rules are applied before filtering, with the extracted `currentValue` and `lockedVersion`
  (`lookup/index.ts:237`).
- A regex `matchCurrentVersion` is tested against `lockedVersion ?? currentVersion ?? currentValue`
  (`lib/util/package-rules/current-version.ts:30-35`).
- H1 is therefore one more rule, `{ matchDatasources: [nuget, npm, crate, packagist], matchCurrentVersion: '/-/',
  ignoreUnstable: false }`. It only touches packages that are already on a prerelease.
- A later rule `{ matchPackageNames: <stable-track globs>, ignoreUnstable: true }` implements the `track: stable`
  opt-out.
- The datasources are restricted to SemVer ecosystems, where `-` always marks a prerelease. Docker tags
  (`20-alpine`), Maven (`33.0.0-jre`) and Go pseudo-versions use `-` for other things.
- D3 is narrowed to what the engine itself drops: a package whose current version is stable gets no line for
  prereleases it isn't offered, because that would add one line per package to every PR.

**SP3 (done): a defect.**
- `BuildManifest.ResolvedVersions` (`CodeCoverage.Versioning/Build/BuildManifest.cs:132-139`) reads `project.assets.json`'s
  `libraries`, which is shared by every target framework, and keeps the last version per id (`resolved[id] = version`).
- When a package resolves to `1.0.0` under one TFM and `2.0.0-beta` under another, the label depends on file order.
- D8 says the least mature wins. Fix (D5): keep the least mature version per id. Test B4 runs on a real restore of a
  two-TFM project with conditional references.

**SP4 (done): no duplicate-TFM bug.**
- A TFM list's channel is its highest token (`locations.ts:112-118`). Tools' `net10.0;net11.0` therefore sits on 11.0,
  and Tools is already "on .NET 11".
- `replace` swaps the from-channel token and removes duplicates (`applyPlatform.ts:41-43`): `net10.0;net11.0` 11 → 12
  gives `net10.0;net12.0`, and `net11.0;net12.0` gives `net12.0`.
- Only `add` is tested (`platform.test.ts:223-228`); C7 adds `replace` over several modern tokens.

**SP5 (done):**
- A real `dotnet pack` (SDK 11.0.100, scratch project `Version 1.0.0` → `Octokit.GraphQL 0.4.0-beta`) gives
  `warning NU5104` with exit 0, and the package is created.
- With `-p:TreatWarningsAsErrors=true`, it gives `error NU5104: Warning As Error` with exit 1.
- B8 asserts both.

**SP6 (done): R16 has a hole for platform moves.**
- `workflowFiles` is built from `report.Groups.SelectMany(g => g.Items)` (`DependencyUpdateEngine.cs:1396`).
- A platform group's `Items` are only its companion riders (`DependencyPolicyEngine.cs:708`).
- The `setup-dotnet` pins the move edits in `.github/workflows/*` are therefore never checked by R16.
- The fix is an owner decision (Q5).

## 8. Plan

One pull request in Spark, as is the rule. The Tools changes go through an issue on Tools: the bot and this repo don't
edit `coverage.yml` there.

- [~] **M0 Unblock Tools now (no Spark code).** Filed as MintPlayer.Dotnet.Tools#211 (2026-11-15). Open items: the
  `coverage.yml` change, the re-run, and the run id here.
  - File the issue on MintPlayer.Dotnet.Tools for versioning-beta-upstream M4: set
    `versioning.packages.MintPlayer.GraphQL: { prerelease: stable }`, then re-run `publish-release` on master.
  - The same issue lists the platform move: `/approve platform:dotnet 11.0` on the next dependency PR, or (Q2 = B) list
    the benchmark csproj under `platforms.dotnet.exclude`.
  - Mark versioning-beta-upstream M4 done with the run id once the 40 packages are tagged.
- [x] **M1 Spikes SP1-SP6** (2026-11-15), results in §7.1. No production code.
- [x] **M2 D1, the PR body's Reason column** (red first: C8 for each reason kind). Test
  `DependencyPullRequestBodyTests.Awaiting_approval_says_why_each_group_waits` (threshold, R7 platform major, P11): red
  1 failed / 6 passed, green 7/7 (2026-11-15). The header's "Over the auto-merge threshold" claim is gone;
  `DependencyPullRequestBody.ApprovalReason` drops the `: comment '/approve …'` the Comment column repeats.
- [x] **M3 D2, the engine applies `PackageTrack`** (red first: A4, A5).
- [x] **M4 Q1, prerelease-current packages and D3, the report line** (red first: A1, A2, A6, A7; I tests on the real
  fixture). The design follows SP2. Done 2026-11-15, M3 and M4 together:
  - C#: `DependencyPolicyConfig.PackageTrackOf` (track + the setting that decides it; H1 prerelease-current, preview SDK
    companions, enforced ceiling), `IsPrerelease`/`PrereleasePattern`/`SemVerEcosystems` (H5), `StableGlobs()`. The engine
    screens a prerelease candidate in `Candidates` (`PreviewNotFollowed`, "a prerelease; <why>"), and `EvaluateGroup`
    lists the newest dropped prerelease even when a stable one beside it is planned (D3).
  - The platform table carries `"stable"` (`DependencyPlatformTable.Json`); the action reads it (absent = `[]`, the
    server and the action's moving tag deploy separately) and `renovateConfig(unstable, stable)` emits the three H5 rules
    in order.
  - Tests: `PackagePrereleaseTrackTests` (A1-A8, the Docker hyphen guard, the pattern theory, the companion and the
    stable-globs cases). Red: 5 failed / 13 passed (A1 and A8 first failed on R17, `0.3 → 0.4` is a major: the tests
    admit majors, which is what they're about). Green: the whole `CodeCoverage.Tests.Dependencies` namespace 576/576;
    `PlatformLifecycleTests` + `DependencyPreviewToReleaseEndToEndTests` 42/42 (asserts the table's `stable`). Vitest:
    `renovate.test.ts` + `platform.test.ts` red 9, green 99/99, typecheck clean. The I test
    (`docker-renovate.integration.test.ts`, real nuget.org, `0.3.0-beta` offered `0.4.0-beta`, and not with a stable glob)
    is skipped on Windows and runs in CI's docker+renovate slice.
- [x] **M5 Q2, P11 refinement** (red first: C2; regression guard C4; C1, C3), **and H6/H7, every workflow moves
  together** (red first: C12, C13, C14; C11 as an enqueue-gate guard). New location kind `workflow-value` in
  `types.ts`, `platform-record.schema.json`, `applyPlatform.ts` (edited like `setup-action`, follows when auto); the
  engine's P11 also holds a `setup-action` location whose reason is `unresolved expression`. Done 2026-11-15:
  - C#: P11 (`DependencyPolicyEngine`, the `unedited` list) skips a computed `tfm`/`tfm-property`/`template-framework`
    on the current channel when the target is that channel (`ChannelOnlyKinds`), and holds a channel-less location whose
    reason is `PlatformLocation.UnresolvedExpression`. The detail now reads "N location(s) the bot cannot edit would stay
    behind (…)", since an unresolved one has no channel. `PlatformRecordPolicyTests`' P11 test used a `$(Tfm)` TFM on an
    in-channel move, which H2 now lets through; it holds on a computed docker pin instead.
  - `PlatformMovesTogetherTests` (C1, C2 ×3 kinds, C3, C4 ×2, C14, the resolved/caller cases ×3): red 5 failed / 6 passed,
    green; `CodeCoverage.Tests.Dependencies` 587/587. C4 shows #617 D36: while 12.0 waits on P11, the 11.0 release goes.
  - TS: `resolveExpression`/`scanWorkflowValues` in `locations.ts` (an `env:` value, an input's `default:`; empty
    default = `an input the caller sets`; anything else `unresolved expression`). A `${{ … }}` with quotes inside was not
    found as a location at all (the key regex refused the quotes), a silent partial move; it is now found and holds.
    Vitest "every workflow moves together (H6/H7)": red 9, green; the whole action suite 451/451, typecheck clean.
  - C11 needs no code: SP6 showed R16 never sees a platform move's workflow edits, which is what H6 decided.
- [x] **M6 Q3 = B (H3): the platform-major opt-in** (2026-11-15). Done:
  - Key `dependencies.platforms.<sdk>.autoMerge: major`; `major` is its only value, anything else is a config error
    (`DependenciesYml.cs`, `DependencyPlatformSettings.AutoMergeMajor`).
  - `DependencyPolicyConfig.RawThreshold` returns `major` for an opted-in `platform:<sdk>` (`PlatformAutoMergesMajor`),
    so `Threshold`/`Inclusion` admit the move without `/approve`, and `Ceiling` still caps it.
  - The group flag: `AutoMerge = level <= threshold && (level != Major || settings.AutoMergeMajor) && !approved`.
  - The enqueue gate: `DependencyEnqueueInput.PlatformMajorOptIns`, filled from `section.Platforms` in
    `DependencyUpdateEngine`. The refusal now names the key ("auto-merges only with `dependencies.platforms.<sdk>.autoMerge: major`").
  - D8 found on the way (see §4): fixed in `AccountMatches`.
  - Tests: `PlatformMajorOptInTests` (C5, C6, C10 opt-in, C10 enforced ceiling × 3 match spellings, another SDK's
    opt-in, value validation × 2), plus two gate tests in `DependencyLifecycleRulesTests`
    (`An_opted_in_platform_major_enqueues_and_another_sdks_does_not`, `…_still_meets_the_other_gates`). Red: build failed
    (no `PlatformMajorOptIns`), then C10 enforced red on D8. Green: Dependencies namespace 598/598.

  The plan as written before M6:
  - A repository may write `platforms.<sdk>.autoMerge: major` in its own `coverage.yml`. The exact key is to be settled
    against `DependencyPlatformSettings` in M6.
  - A platform major is then auto-merged when every other gate passes: companions, P11, cooldown, fleet red, and an
    enforced account ceiling, which still wins.
  - Default unchanged: R7 holds for every repository that doesn't opt in.
  - Red first: C5 (default still waits), C6 (`allowMajor` alone still not auto-merged), and C10 (the opt-in
    auto-merges; an enforced account `maxUpdate: minor` still refuses it).
  - Mark D-P18/R7 in `dependency-updates-PRD.md` "amended by H3" and the comment at `DependencyPolicyEngine.cs:703`.
  - **R7 is enforced twice.** Both places must honour the opt-in:
    - the group flag at `DependencyPolicyEngine.cs:704`;
    - the enqueue gate `DependencyAutoMergeGate.Refusal`, which refuses any platform-major group a second time
      (`DependencyLifecycleRules.cs:239-240`, "moves a platform major, which never auto-merges").
  - The opt-in doesn't lift the other enqueue gates (`DependencyLifecycleRules.cs:229-254`, `DependencyUpdateEngine.cs:1405-1408`).
    The docs must list them next to the key:
    - the merge queue enabled, and not the onboarding draft;
    - every applied group auto-mergeable, so one approved group anywhere in the rolling PR stops the whole PR (an
      approved group is never `AutoMerge`, `:704`);
    - no code migrations (an `onMajor` migrate workflow);
    - no lockfile "needs a human" verdict;
    - no edited workflow that no `pull_request` run tests (R16);
    - the coverage gate (`maxCoverageDrop`);
    - not a run started by a comment (`/refresh`, `/approve`), because only a scheduled run auto-merges.
  - **SP6 (spike):** does R16 see the platform move's workflow edits? `workflowFiles` comes from
    `report.Groups.SelectMany(g => g.Items)` (`DependencyUpdateEngine.cs:1396`), and a platform group's `Items` are only
    its companion riders (`DependencyPolicyEngine.cs:708`), so the `setup-dotnet` pins it edits may be invisible to R16.
    In Tools that matters: the move edits `dotnet-build-master.yml`, `dotnet-build-any.yml`, `eid-deploy.yml` (push)
    and `assertions-benchmark.yml` (schedule). None of them run on `pull_request`.
    - If R16 sees them, Tools' SDK moves can never auto-merge, opt-in or not.
    - If it doesn't, R16 has a hole for platform moves.
    
    Either way, a test (C11) and a decision follow.
- [x] **M7 Versioning: D4 message plus B2-B9** (2026-11-15).
  - D4: `VersionCalculator` refuses with "Pin the label in coverage.yml: versioning.packages.<id>.prerelease: stable (or
    <line label> to stay on the line)"; `VersionRequest.PackageId` carries the id from `ReleasePlanner`.
  - D5: `BuildManifest.LessMature` keeps the least mature version per id (non-semver yields; a tie keeps the first).
    Red: B4 on a real restore failed (4 tests). The fixture `Fixtures/MultiTfm/project.assets.json` is a real
    `net10.0;net11.0` restore (Newtonsoft.Json 13.0.4 vs 13.0.4-beta1, which NuGet lists beta first), trimmed to
    `version`/`targets`/`libraries` so no local path ships. Green: `BuildManifestTests` 16/16.
  - `ReleaseLabelTests`: B2 (message names `versioning.packages.Core.prerelease: stable` and "rc"), B3, B5, B6, B7, B9
    (Tools-shaped: GraphQL beta + Api over GraphQL + Util. Without the pin only GraphQL is refused, because Api declares
    only GraphQL (B7). With the pin all three tag `11.0.0`). Green 39/39 with `VersionCalculatorTests`. B2 was written
    after the message, and reverting the message gave a compile error, not an assertion red; the old text has no
    `versioning.packages`.
  - B8 in `RealPackTests` (project P outside the solution, Newtonsoft.Json 13.0.4-beta1): exit 0 + `warning NU5104` +
    the nuspec dependency; `-p:TreatWarningsAsErrors=true` gives `error NU5104` and no package. 10/10. These pin
    existing behaviour; there's no code change.
- [x] **M8 Platform multi-target C7** (2026-11-15). `platform.test.ts` "strategy replace on a multi-modern-target list…
  (C7)": `net10.0;net11.0` 11 → 12 gives `net10.0;net12.0`; `net11.0;net12.0` gives `net12.0`; netstandard/net48 kept;
  plus a real `Directory.Build.props` apply. Passed first time, as SP4 predicted: no bug.
- [x] **M9 Docs** (2026-11-15). `dependency-updates.md`: package prereleases (H1, Octokit example, `track: stable`),
  account-rule match spellings (D8), every workflow moves together (H6/H7, `workflow-value`, unresolved expressions,
  the `pull_request` coverage note), the in-channel refinement (H2), the Awaiting approval reasons table, and
  `autoMerge: major` with the gates it doesn't lift. `versioning.md`: NU5104/`NoWarn`, the refusal's key, B3/B7, and
  per-TFM resolution. Supersede marks: `platform-locations-PRD` P11, `sdk-updates-PRD` D8, `dependency-updates-PRD`
  R7/D-P18.

  The plan as written:
  - `dependency-updates.md`: package track with an Octokit example, the prerelease-current rule (Q1), and the approval
    reasons table.
  - The versioning guide: the `stable` pin for "a stable library over a preview package".
  - Fix the `UnstableGlobs` doc comment.
  - Supersede-mark P11/D-P18 text where Q2/Q3 change it.
- [x] **M10 Gate.** Push, open the PR, mark it ready, watch the checks. The full suite runs on CI, not locally.
  Afterwards, ask for coverage through the codecoverage MCP.

  Evidence: PR #671, commits 9424b367c + 8790e99ae. The first run failed only on the setup page's snippet spec (the
  `autoMerge: major` line missing from `dependency-updates-setup.snippets.ts`), fixed in 8790e99ae. After that, 25 checks
  passed and 5 were skipped. Patch coverage was 98.6%; the uncovered lines are `DependencyPolicyConfig.cs:142` and
  `locations.ts:435`.
- [x] **M11 The Tools release (M0's follow-through).** Tools #212 (the `prerelease: stable` pin, closes #211) merged.
  Its publish-release run 43711348322 tagged the 40 GA versions but packed the rc ones (D9-D11). Fixed in #671 with
  c4b6c1412, 469571934 and 85d653bbd, each red → green. Attempt 2 of the same run published the GA packages: 41 pushes,
  e.g. `MintPlayer.GraphQL.11.0.0`, `MintPlayer.SourceGenerators.Tools.13.0.1`. That attempt succeeded before the
  deploy, because its checkout fetched the tags the first attempt had pushed. #671 was queued on the owner's word
  (2026-11-15).
- [ ] **After deploy.** The next Tools dependency pull request moves the SDK `11.0.100-rc.1` → `11.0.100` (H2). Verify it,
  then close Tools#211 if #212 didn't.

## 9. Test plan summary

- **New unit classes:**
  - `PackagePrereleaseTrackTests` (A1-A8)
  - `PlatformApprovalReasonTests` or additions to `PlatformRecordPolicyTests` (C1-C6)
  - body tests in `DependencyLifecycleRulesTests` (C8, A7)
  - `ReleaseLabelTests` additions (B2-B7, B9)
- **Real fixtures** (the parsers need real data, never invented shapes):
  - the Octokit.GraphQL `0.3.0-beta` csproj for SP1/A1 (I)
  - a real `project.assets.json` from a multi-TFM project for SP3/B4
  - a real pack for B8
- Each scenario row in §5 names its test; a row without a test is not done.

## 10. Risks

- Q1 = A changes what every onboarded repo on a prerelease receives, which means more PR churn. The mitigation is a
  `track: stable` rule, documented next to the change.
- Changing the Renovate lookup to unstable-for-all (SP2 option) may change the candidates for stable packages. SP2 must
  prove it doesn't, or the design keeps per-package `packageRules`.
- P11's refinement must not let a really half-done move through; C4 guards that.
- H7 known limit: a caller that passes a literal SDK version to a local composite action or reusable workflow
  (`uses: ./.github/actions/x` with `dotnet-version: 10.0.x`) is not scanned, so that literal stays behind. Spark's own
  callers pass nothing (the input falls back to `global.json`). Scanning callers' `with:` is a separate issue if a
  repository needs it.

## 11. Spike evidence and decision log

| # | Decision / evidence | Source |
|---|---|---|
| E1 | `PackageTrack` has no caller; the `UnstableGlobs` comment (`DependencyPolicyConfig.cs:141-143`) claims it does. | grep over `apps/CodeCoverage`, 2026-11-15 |
| E2 | PR #210's in-channel move waited for P11, not the threshold; the body shows no reason. | `DependencyPolicyEngine.cs:594,671-690`; `DependencyPullRequestBody.cs:64-69`; PR #210 body (Not moved: 2 conditioned TFMs) |
| E3 | 40-package GA blocked by GraphQL's beta label; the last `publish-release` is still run 43509498988. | `gh run list` on Tools master, 2026-11-15 |
| E4 | PR #210 closed by the owner's account, not the bot. | `issues/210/events`, 18:40:57Z |
| E5 | The app wrote the tags and then answered 500. `MintPlayer.GraphQL/v11.0.0` has tagger "CodeCoverage versioning" (the app's tagger; the local CLI tags as github-actions[bot]) and date 12:01:07Z. The action logged "unavailable (the app answered 500)" at 12:01:09Z. | `gh api …/git/tags/<sha>`; job 132803371304 log, line 3507 |
| E6 | The 500 was the session cap. Production log: `InvalidOperationException: The maximum number of requests (30) allowed for this session has been reached`, at `UpstreamReleases.RecordAsync` (`UpstreamReleases.cs:75`) ← `VersioningController.FinishAsync:278`. | `docker compose logs coverage-app`, 12:00:30-12:01:30Z |
| E7 | Rule: past 30 requests a session batch-loads (`LoadAsync<T>(ids)`) or eager-loads. | owner, 2026-11-15 |
| H1 | Q1 → A: a package whose current version is a prerelease follows newer prereleases on the default track; a `track: stable` rule opts out. Amends #606 D8 for packages. | owner, 2026-11-15 |
| H2 | Q2 → A: a computed location whose value already names the target channel holds nothing; P11 still holds what would stay behind. Refines P11 (`platform-locations-PRD.md:564`). | owner, 2026-11-15 |
| H3 | Q3 → B: an explicit per-repository opt-in auto-merges a platform major; the default and the enforced account ceiling are unchanged. Amends D-P18/R7. | owner, 2026-11-15 (against the recommendation to keep D-P18) |
| H4 | Q4 not asked: D2 is fixed as a defect, because the `UnstableGlobs` comment already states that the engine screens candidates against `PackageTrack`. | this PRD, §4 |
| H5 | Renovate design for H1: one `packageRules` entry `{ matchDatasources: [nuget, npm, crate, packagist], matchCurrentVersion: '/-/', ignoreUnstable: false }`, then a later `{ matchPackageNames: <stable-track globs>, ignoreUnstable: true }`. Not "unstable for everything": that would displace stable candidates from Renovate's buckets. D3 is narrowed to prereleases the engine itself drops. | SP2 (§7.1): Renovate 44.125.2 `filter.ts:166-193`, `bucket.ts:38-82`, `lookup/index.ts:237,427-446`, `current-version.ts:30-35` |
| H6 | Q5: a platform move's workflow edits are **not** refused by R16. "It's up to the repo-owner to provide a pull-request workflow that covers the other workflows. We can't just update a single workflow to use the latest sdk and leave the rest in place, all workflows must move together." So D6 stays open on purpose, and a move must not leave a workflow behind (D7, C12-C14). | owner, 2026-11-15 (question dialog, verbatim) |
| H7 | D7's scope: `env.X` (any `env:` mapping in the same file) and `inputs.x` with a literal `default:` (an `action.yml` `inputs:` or `on.workflow_call.inputs`) become a `workflow-value` location that moves like a `setup-action` pin. An input with an empty or missing default is the caller's: the caller's `with:` on a local action is not scanned (known limit, §10), and holding on it would hold Spark's own moves forever (`.github/actions/lockfile-regenerate/action.yml:36-39,75`, `default: ''` = read `global.json`). Any other expression holds the move with reason `unresolved expression`. | this PRD, from H6 + the Spark composite actions |
