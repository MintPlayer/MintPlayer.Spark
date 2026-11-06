# Backporting to release lines (#568) — plan

Spec: [coverage_backport_PRD.md](coverage_backport_PRD.md). Tests run once per PR, at its end. Each PR runs its own
spikes first.

## Resume here: #554 (2026-11-06)

- **Branch:** `feat/issue554-rulesets-oauth`, a fresh start from master `b6b525ffb` (owner, 2026-11-06: "Start fresh
  on a blank branch"). It is local and has never been pushed, has no upstream, and no PR exists. Push it, as a draft
  PR, only when the owner asks.
- **The old branch `feat/issue554-rulesets` is abandoned.** It holds e0–e3, e5 and e6, which were built on
  Administration and are largely superseded. It stays local, unmerged. None of its code is on this branch: not e0's
  scoped tokens, not e1's planner and adapter, not e5's enqueue refusals, and not the retry-form attribute
  descriptions (`80a3d1e60`). Their notes under M10e stay as findings to reuse.
- **State:**
  - Administration is removed from both GitHub Apps; all four installations were checked.
  - The design is a one-shot token from a new OAuth app. See "#554 redesign" under M10e below.
  - f1 (the one-shot OAuth token in Spark.Authorization and ng-spark) is built.
- **Next:**
  - f0, the live spikes S-A to S-F. They wait for the owner's go-ahead: a test OAuth app and a throwaway public
    and private repository.
  - Then f2–f6.
- **Open for the owner:** f4. Should the chooser apply the rulesets when it is confirmed, with the same popup
  (proposed), or only point to the "Set up rulesets" action?

## Three pull requests (owner exception to the one-PR rule, 2026-11-05)

One PRD and one plan; three concise PRs, merged and deployed in order. Each one is deployable and useful alone, and no
repository is left unable to release in between.

| PR | Scope | Spikes | Milestones | Done when |
|---|---|---|---|---|
| **1. Release lines** (#569) | Lines exist, build, version and publish; master stops releasing where enabled | S3, S6, S9, S10 | M1, M2, M3, M4, M11 (workflow triggers, guards, ruleset; no lines cut yet) | A test repo with `release/dotnet-N.x` releases from the line only; the lifecycle page is live |
| **2. Backports** (#570) | The resilient backport: markers, resolvers, regeneration, per-line outcomes, merge queue for lines, labels/reporting | S1, S2, S5 | M5, M6, M7, M8 (without D11), M11 (cut Spark's four lines + seed tags, enable D15 for Spark) | A Spark PR lands on `release/dotnet-11.x` and `release/dotnet-10.x` through the queue, and releases |
| **3. Dependencies and onboarding** (#571) | Line-aware dependencies and the feature-chooser onboarding with branch renames | S4, S7, S8 | M8's D11 part, M9, M10, M12 leftovers | Onboarding a MintPlayer library repo renames its old lines and sets everything up; rollout (C2) starts |

Ordering constraint: Spark enables "release only from lines" (D15) **in PR 2**, not PR 1, because once enabled, a
change ships only after it has been backported. Docs for each PR land in that PR (M12 is split accordingly).

## Spikes

### S1 — Conflict census (evidence for everything else)

1. Reconstruct a .NET 10 line: the last master commit before the net11 move (first seen in
   `libs/spark/MintPlayer.Spark/MintPlayer.Spark.csproj` at `61c5c4276`, 2026-09-24; confirm the real move commit).
   Do this in a throwaway bare clone in the scratchpad, never in the main checkout.
2. For each squash merge on master after it (first-parent; ~100), run
   `git merge-tree --write-tree --merge-base=<c^> <line tip> <c>` and, when the result is clean, advance the line tip
   (simulates backporting everything).
3. Record clean / conflicted, and the conflicted paths by category (marker file, generated `App_Data`, lockfile,
   source).
4. Repeat with a prototype normalization (S2) and with the merge queue's resolver rules.

Output: a table in the PRD (§8 evidence) with the percentages. Done when the categories add up to 100 %.

### S2 — Marker normalization prototype

**Status (2026-11-05): built.** It lives in `CodeCoverage.Git/Backports/PlatformMarkers.cs`, with tests in
`PlatformMarkersTests` that merge with the real `git merge-file`. One deviation: the signature is
`Normalize(path, ours, base, theirs)`, not `(path, blob, targetMarkers)`. S1's per-occurrence safety rule compares base
with theirs, so one blob at a time can't decide. The line's own blob (ours) supplies the target values, so no catalog
of target values is needed. Wiring it into `BackportService` is M5. The evidence is in PRD §8.3, "S2".

- A pure function `Normalize(path, blob, targetMarkers) → blob` for csproj/props (XML),
  `global.json`, `package.json`, workflow YAML.
- Tests, starting from research note 2's experiments:
  - B (Nullable next to the TargetFramework line) merges clean and keeps `disable`;
  - C (`global.json` bump only) gives an empty result reported as "marker-only";
  - a real edit on a marker line itself (net10 → net10 + net11 multi-target) must still **conflict**, never vanish.
- Byte fidelity: no reformatting outside the rewritten value. Line endings and indentation are preserved, as the
  `Diff3Document` rule (no zdiff3, no folding) requires.

### S3 — Ecosystem-scoped lines in the engine

- `ReleaseBranches` parses `release/<ecosystem>-<N>.x`. `BranchMajor` applies only to anchored packages of that
  ecosystem. Other ecosystems' packages are excluded from the plan with an informational note, and free packages stay
  skipped.
- Red→green tests in `CodeCoverage.Versioning.Tests` (ReleasePlanner): a mixed repository on `release/dotnet-10.x`
  releases NuGet 10.x and leaves ng-spark out. Today's behaviour is a refusal of the whole run, the red.

### S4 — Dependency versions when packing on a line

- Scratch repo with two projects, A → B (`ProjectReference`), tags `B/v10.1.0` on a line and `B/v11.0.0` on master.
  Pack A on the line. Is the nuspec dependency `B >= 10.1.0`?
- Measure the cost of emitting `[10.1.0, 11.0.0)` (a target in `MintPlayer.Versioning.MSBuild`), and what NuGet
  restore does for a net10 consumer that also references B 11 (NU1202 / NU1608).
- npm: ng-spark's peer ranges are carets, so they already cap the major. Check the line's `@mintplayer/ng-bootstrap`
  range is a marker rewritten by S2.

### S5 — Live: CI and regeneration on a backport branch

On `PieterjanDeClippel/SparkTest` (scratch repo from #517), with a `release/dotnet-10.x` branch:

- Does the App-opened backport PR start `pull_request` CI on the line (App tokens trigger workflows, `GITHUB_TOKEN`
  doesn't)?
- Dispatch the line's `regenerate.yml` on `backport/…`, apply its patch, and check the lockfile has the line's
  versions.

**Result (2026-11-06, live on production, new scratch repo `PieterjanDeClippel/SparkTest`; the #517 one was
deleted):** after the S7 rename, PR #3 into `main` (a `fix:` adding `Greeter.cs`) was labelled
`backport: dotnet-10.x` when it opened (`defaultLabels`), with the **Backport targets** check "1 line applies:
dotnet-10.x". Merging it opened backport PR #4 (`backport/3-dotnet-10.x` → `release/dotnet-10.x`, by
`coverageproduction[bot]`) and the comment "Backports: `release/dotnet-10.x`: #4".
- **CI on the line: yes.** The App-pushed branch started `pull_request` CI (run 42186052478, `build` success).
- **Regeneration: not exercised.** The scratch repo has no `mergeQueue`, `regeneratedPaths` or lockfile, so that
  half is still open live; it needs a repository with the merge queue's regenerate workflow and a lockfile.

### S6 — npm on an older major

- Does `MintPlayer/github-actions/publish-npm-packages` honour `publishConfig.tag`?
- Dry-run `v21-next` for an older-major prerelease.

### S7 — Renaming branches as an App (D2)

- Which App permission does `POST /repos/{o}/{r}/branches/{b}/rename` need for a non-default branch (Contents vs
  Administration)? Try it on the scratch repo with the dev App.
- Check what GitHub does with open PRs based on the old name, branch protection/rulesets targeting it, and the
  old-name redirect for `git fetch`.

**Result (2026-11-06, live on production, scratch repo `PieterjanDeClippel/SparkTest`):** Set up features with
Package versioning + Release lines planned `release/10.0 → release/dotnet-10.x` (one packable `net10.0`/`net11.0`
csproj). Merging onboarding PR #2 renamed it within seconds.
- **Permission:** Contents: write is enough. CoverageProduction holds no Administration permission (installation
  page: read/write on actions, checks, code, issues, pull requests, workflows) and the rename succeeded.
- **Open PRs:** PR #1 (`fix-on-10` → `release/10.0`) was retargeted to `release/dotnet-10.x` and stayed open
  (`gh api …/pulls/1`: `base.ref = release/dotnet-10.x`).
- **REST API:** `GET /branches/release%2F10.0` answers 200 with the renamed branch.
- **`git fetch` does not follow:** `git fetch <url> release/10.0` → `fatal: couldn't find remote ref release/10.0`.
  The docs, the guide page and the onboarding PR body said GitHub "redirects the old names"; corrected to say clones
  must switch.
- **Rulesets/branch protection on the old name:** not tested; the scratch repo had none. Rulesets are #554's.

### S8 — Inventory of today's onboardings (D3)

- List every per-feature onboarding (pages, PRs, coverage.yml sections, App permissions each needs).
- Find the shared parts and what a single chooser needs. Read-only, done before M2.

### S9 — Concurrent releases from several lines (D17)

- Prove or refute: with one line per ecosystem major (D1), two lines never compute the same version, so a fingerprint
  scoped to the plan's packages on the line's major (plus in-repo dependencies) is safe.
- Red→green test: two lines plan at once, one releases, the other still releases (today: "version tags changed").
- Collision test: a deliberately stale plan on the **same** line is still refused.
- Pick the design against D17's criteria (a)–(d), and record the evidence in the PRD.

### S10 — Platform lifecycle sources (D4)

- Inventory and field mapping per source: dotnet `releases-index`, endoflife.date for angular, react, vue, nodejs,
  php, …
- Rate limits, and what each source says about previews and LTS.
- Reuse the dependency updater's `PlatformCandidate` shape where possible.

## Milestones (after the grilling, D1–D18)

**Status 2026-11-05 (#569, merged as PR #572 at `e1908e0e5`):** M1, M2 (lines pattern, `packages.<id>.line`,
per-line prerelease), M3 (collector, page), M4 and M11 (triggers, schema revision) are implemented and merged; see PRD
§8.2. Next: #570 (spikes S1, S2, S5; M5–M8 without D11; Spark's lines cut per M11; plus the items moved below) on
`feat/issue570-backports`. **#570 progress:** S1 done (PRD §8.1: plain 61/72 applied, normalized 72/72, every
conflict a platform value); the merge queue's MSBuild keep-both now checks keys (PRD §8.3); `keyed-merge` built
(`ea532ec93`, opt-in, PRD §8.3). **Keyed-merge milestones (owner: "small wins toward the end goal", 2026-11-05):**
K1 a package version takes the highest only when **both sides raised it** (a downgrade on either side refuses); the
release-line cap (nothing above the line's platform) lands with M5, where the backport gives the resolver its line; K2 one dialect per file format behind a seam (file pattern, entry
reader, version order), `composer.json` the second JSON dialect; K3 **stop**: a further format (Maven, Gradle
catalogs, PEP 440, go.mod, Cargo) is built only for a repository that needs it, with real fixtures (issue). Lockfiles
stay take-base + regenerate (presets). Intent replay (re-run the updater's bump on the new base) goes to #571.
`Directory.Packages.props` is an optional onboarding suggestion, never required. Moved to #570: per-line keep/retire, the
retire-suggestion PRs, and the M2 keys whose consumers are backports (`defaultLabels`, `onConflict`, `markers`,
`enqueue`). Rulesets (#554's action plus the release-line ruleset, with the user's own token) move to M10 in #571,
PRD §8.2.

- **M1 Docs truth.** Fix the stale "not yet deployed" (README). Correct versioning D10 to match `BackportService.cs`
  (G1), and mark D10/D12's default-branch release and `release/*.x` superseded by D1/D15. Copy the research notes
  into `docs/research/`.
- **M2 Config.** Release-lines section in coverage.yml (enable, configurable pattern with `{ecosystem}`/`{major}`
  (D2), `defaultLabels` (D5), `onConflict` (D7), `markers` (D8), `enqueue` (D12), per-line keep/retire and
  prerelease override (D4, D14)), plus `dependencies.lines` (D13). Opt-in; repos without it are unchanged (D18).
- **M3 Platform lifecycle.** Scheduled collector (S10), a public read-only Spark persistent object, query and program
  unit (D4), and retire-suggestion PRs.
- **M4 Engine.**
  - Ecosystem-scoped lines (S3), and releasing only from lines when enabled (D15).
  - Auto-cut creates the new major's line (D15).
  - Concurrency per S9 (D17), and the `v<major>-next` dist-tag.
  - Per-line prerelease.
- **M5 Writer.**
  - Marker normalization (S2, D8) plus the resolver registry in `BackportService` (G1). **Normalization done
    (2026-11-05).** **Resolvers done (2026-11-05):** the merge queue's resolver, with the default branch's
    `mergeQueue.autoResolve` rules (O16), as PRD §8.3 records.
  - Independent per-line outcomes (D6), with an empty result reported as already present. **Done (2026-11-05).**
  - Draft-with-markers mode (D7). **Done (2026-11-05)** through `backport.onConflict: draft`, along with
    `backport.markers` (D8). The per-request `/backport --draft` moves to M8. Details are in PRD §8.3, "M5, third
    part".
- **M6 Regeneration on backport branches** (S5, D9): dispatch the line's workflow, apply the patch, then open the PR.
  **Done (2026-11-05).** The workflow's provenance is the line tip's copy, and every regenerated path the change
  touches triggers it (owner decisions). Any stop opens the PR with a "Not regenerated" note. The live check is S5.
  Details are in PRD §8.3, "M6, built".
- **M7 Merge queue for lines** (D12), **done (2026-11-05)**:
  - `mergeQueue.branches` patterns / `supported-lines`. "Supported" is every match of the line pattern until M8's
    `keep`/`retire`.
  - automatic enqueue of backport PRs (`backport.enqueue`, default true). Drafts and "Not regenerated" PRs are never
    enqueued.
  - the line's own regenerate workflow on its queue: provenance against the line tip.

  Details are in PRD §8.3, "M7, built".
- **M8 Labels and reporting.**
  - **M8a supported lines** (D4, D16), **done (2026-11-05)**: branches from the forge, `PlatformRelease` phases,
    `backport.lines` `keep`/`retire`; `supported-lines` refuses an unsupported line at enqueue. Details are in PRD
    §8.3, "M8a, built".
  - **M8b default labels (D5, the current line always, D16) and the "Backport targets" check run, done
    (2026-11-05)**: the writer's ecosystems job, `defaultLabels`/`fixLabels`, line names resolved for backports.
    Details are in PRD §8.3, "M8b, built".
  - **M8c provenance, done (2026-11-05)**: the `<!-- backport-of: owner/repo#N -->` marker in every body the app
    writes, `backported: <line>` on the source when a backport merges, hand-made backports recognised (body, head,
    `cherry-pick -x` trailer) and linked by a comment, and the pull request opened when a person creates
    `backport/<pr>-<line>` (D7). Details are in PRD §8.3, "M8c, built".
  - **M8d `/backport <line> --draft`, done (2026-11-05)**: the per-request draft (D7), and the conflict comment names
    it. Details are in PRD §8.3, "M8d, built".
  - **M8e cross-repo dependency diagnosis (D11), done (2026-11-05)**: built in #570 rather than #571, since it only
    reads the backport's own result. Normalization reports the first-party packages it dropped; a versioning run records
    which repository releases each package (`PackageReleaseRecord`); the backport comment names the upstream release
    and which of its pull requests the upstream already backported to the line's major. Details are in PRD §8.3,
    "M8e, built".
- **#571 progress (2026-11-05, `feat/issue571-dependencies-onboarding`):** S4 and S8 done (PRD §8.4). D10b's default
  needed nothing. Its opt-in `versioning.dependencyRanges: major-bounded` is built: an MSBuild target, the config key,
  and the action's exported variable. S7 is a live check, still to do.
  M9 built and its .NET tests green. The guide-page specs pass (2026-11-06). Their earlier failure was the run
  command, not the code: page folders passed as test roots made the builder compile `.html`/`.scss`. Pass
  workspace-relative `--include=<…>.spec.ts` paths instead.
  **M10 sub-milestones (decided 2026-11-06, PRD §8.4 "M10"):**
  - **M10a, coverage.yml sections:** one shared editor inserts a top-level section in alphabetical order, or
    replaces it. The versioning and dependency onboardings use it instead of appending.
  - **M10b, the planner:** a pure planner, one rule set per feature over one file overlay of the default branch
    (generalising `VersioningOnboarding`'s rules). It returns the files, the body, the seeds, the renames, the lines
    that need the regenerate workflow, and the settings to apply on merge.
  - **M10c, the chooser:** a "Set up features" action on the Repository page (a checkbox form, then the plan) that
    pushes one `mintplayer/onboarding` pull request. It is rebuilt on every new default-branch head while open.
    It replaces "Set up versioning".
    **Built (2026-11-06):** git writer `POST /jobs/onboarding` (versioning paths plus the dependency set; only
    Dependabot's file may be deleted) onto `mintplayer/onboarding`; `IOnboardingService` (preview, open, rebuild);
    `SetUpFeaturesAction` (virtual `SetUpFeatures` checkbox form, then the plan for a confirmation); a push to the
    default branch rebuilds the open pull request (`OnboardingRebuildEventsRecipient`). The state stays in
    `VersioningOnboardingState` (the production collection), extended with the features, renames and on-merge
    settings, so the existing seed push covers the new branch too. `IVersioningOnboardingService.OnboardAsync` is
    gone; the service keeps `PushSeedsAsync`. Tests: `OnboardingServiceTests` (4), `OnboardingWriterJobTests` (5),
    the client route table.
  - **M10d, on merge:**
    - apply the settings (dependency updates, size labels, coverage);
    - push the seeds;
    - rename the old lines (D2);
    - open a pull request into each line that lacks the regenerate workflow (D9).
    **Built (2026-11-06):** `IOnboardingService.MergedAsync` is the merge's one entry point (owner, 2026-11-06: the
    seeds recipient should not depend on two services). The merged webhook on `mintplayer/onboarding` (or the #153
    branch) reaches it through `VersioningSeedsRecipient`, under the run lock. It pushes the seeds (`PushSeedsAsync`,
    so a #153 pull request still gets them), switches dependency updates (plus the onboard signal) and size labels
    on, and renames each planned line through the new `IForgeMergeQueue.RenameBranchAsync` (GitHub
    `POST /branches/{b}/rename`; a refusal is noted, not retried). Then, for each supported line missing a workflow
    that the default branch's `mergeQueue` names (`Workflows()`), it pushes `/jobs/onboarding` with `line` and opens
    a pull request from `mintplayer/onboarding-<line>`. The writer's line mode accepts only 1–8
    `.github/workflows/` files, with no deletions. `MergeHandledAt` stores the merge commit, so the merge is handled
    once. Coverage has no setting to switch: its step stays a to-do in the body. A plan with no files (size labels
    alone) applies its settings and renames at once, without a pull request. Tests: `OnboardingServiceTests` (+2),
    `OnboardingWriterJobTests` (+3), `GitHubForgeMergeQueueTests` (rename), conformance row.
  - **M10e, rulesets:** a step in the chooser. The plan is shown first, then applied with the user's token, with
    the sign-in popup when that token is stale. **All of #554** (owner, 2026-11-06; PRD §8 M10), **in its own pull
    request after #574 merges** (owner, 2026-11-06), which closes #554:
    - e1: the forge's ruleset/protection API on the user's token, and a pure planner (create, update or repair to
      `exempt`, migrate the classic lock and review count after ruleset 1);
    - e2: the chooser step and the `github.signIn` popup client method (`Retry.Invoke`);
    - e3: ruleset 2 (the default head's checks confirmed, the approval count asked);
    - e4: organization rulesets from the Account page;
    - e5: the enqueue refusal for classic lock and review count, and the eviction hint;
    - e6: merge-queue.md §2.1/§3.

    **Superseded token choice (owner, 2026-11-06): the App's installation token, not the user's.** If the App can
    manage rulesets itself, the in-app GitHub sign-in for a user token goes away (e2's `github.signIn` popup with
    it). Evidence: GitHub's "Permissions required for GitHub Apps" lists `POST /repos/{o}/{r}/rulesets` and
    `PUT …/rulesets/{id}` under repository **Administration: write**, for user and installation tokens (UAT, IAT);
    `POST /orgs/{org}/rulesets` under organization Administration: write. Costs to weigh in #554:
    - Administration: write also covers `DELETE /repos/{o}/{r}` and the other repository settings, on every
      installation.
    - CoverageProduction and CoverageDevelopment held no Administration before #554 (installation page, 2026-11-06),
      so each installation must accept the new permission; until then the step refuses with the acceptance link
      (D95/O17). **Done 2026-11-06:** repository Administration: read+write added to both Apps (not organization
      Administration, owner: "repository administration is enough"), and accepted on all four installations (the
      owner's account and the MintPlayer organization, Prod and Dev).
    - e4 (organization rulesets) needs the organization Administration permission as well.

    No narrower permission exists: every permission heading on that page was checked, and there is no "Rulesets" or
    "Branch protection" heading. Reading needs nothing new: `GET …/rulesets`, `GET …/rulesets/{id}` and
    `GET …/rules/branches/{branch}` are under Metadata: read.

    **#554 design (owner, 2026-11-06), replacing the user-token design above (e2's sign-in popup is dropped).
    ⚠️ Superseded the same day by the OAuth-app design below ("#554 redesign: a one-shot OAuth app token"):
    Administration was removed from both Apps again.**
    1. CoverageProduction and CoverageDevelopment request repository Administration: write. Each installation accepts
       it; until then the step refuses with the acceptance link (D95/O17).
    2. **Ordinary installation tokens exclude Administration.** `GitHubInstallationService` (Spark.Webhooks.GitHub,
       `CreateInstallationToken(installationId)` at the time of writing) passes no `permissions`, so GitHub grants the
       token everything the installation accepted. It must request them explicitly: the installation's own
       `permissions` (`GET /app/installations/{id}`) minus `administration`, not a hard-coded list that would drop a
       permission added later. Through Octokit if it has a permissions overload, otherwise a raw
       `POST /app/installations/{id}/access_tokens`.
    3. **The ruleset step mints its own token:** `permissions: { administration: write }`, `repository_ids: [that
       repository]`. It is never cached, lives only in that one method, and is revoked in a `finally`
       (`DELETE /installation/token`). Installation tokens always last one hour (the lifetime cannot be set, so the
       "3 minutes" asked for is met by revoking), so a failed revocation leaves a token that reaches one repository
       and expires within the hour.
    4. Reading and verifying rulesets uses the ordinary token (Metadata: read).
    5. A test pins it: the ordinary token request never carries `administration` (WireMock on `access_tokens`), and
       only the ruleset method asks for it.
    Remaining exposure: the App's private key can mint Administration tokens for every installation.

    ⚠️ **e0–e6 below were built on `feat/issue554-rulesets`, which was abandoned on 2026-11-06.** The owner had the
    work restart from master on `feat/issue554-rulesets-oauth`, so none of this code is there. The notes stay
    because f2 and f3 reuse their findings: the planner's rules, the classic-protection move, and e5's refusal rules.

    **e0 built (2026-11-06, `edc34e68c`):** items 2, 3 and 5. `GitHubWebhooksOptions.ExcludedTokenPermissions`
    (default empty, so other Spark apps are unchanged; CodeCoverage's `Program.cs` sets `["administration"]`) makes
    every cached token request the installation's permissions minus the excluded ones; a failed read of the
    installation throws instead of falling back to the whole grant. The scoped token is
    `IGitHubInstallationService.CreateScopedInstallationClientAsync`. It returns a `ScopedInstallationClient`: an Octokit
    `GitHubClient` that is `IAsyncDisposable` and revokes its token when disposed, once, as best effort (owner,
    2026-11-06: "a wrapper class for the githubClient that implements IDisposable, and revokes the token when it
    disposes"). It is async-disposable rather than `IDisposable` because the revocation is an HTTP call. The typed
    form is the extension
    `await using var client = await installations.CreateScopedInstallationClientAsync(installationId, repositoryId, [GitHubTokenScope.Write("administration")])`
    (owner: "an extension method that accepts the scopes parameter"). Octokit 14 has no permissions overload for
    `CreateInstallationToken`, so the mint is a raw `POST` with a string body. Tests (red, then green): five `E0_*`
    cases in `GitHubInstallationServiceWireMockTests`:
    - an excluded permission is absent from the mint request body;
    - with no exclusions, the request is unchanged and the installation is not read;
    - the scoped mint carries `{administration: write}` and `repository_ids: [7]`, lives until disposal, is revoked
      with its own token and is never cached;
    - it is revoked when the block throws;
    - a second dispose does not revoke again.

    Octokit sends the revoke's scheme as `Token`.

    **e0 hardened (2026-11-06; owner: "Yes, add all three", asked whether the tests prove no ordinary client carries
    Administration):**
    - **Host test found a real bug.** `InstallationTokenScopeTests` (CodeCoverage.Tests) resolves
      `IOptions<GitHubWebhooksOptions>` from the real host. It failed: "Expected options.ExcludedTokenPermissions to
      contain "administration", but found {empty}". `AddGithubWebhooks` copies each option into `Configure<>` by hand
      and did not copy the new one, so `edc34e68c` as committed would have excluded nothing in production. Fixed in
      `SparkBuilderExtensions`; the test is green after the fix (red → green). No other option is missing from the copy.
    - **Fail closed on a failed read:** `E0_a_failed_installation_read_mints_no_token_and_throws`. GET
      `app/installations/42` answers 500; the call throws, there is no POST to `access_tokens`, and the cache is empty.
    - **The reply decides, not the request:** after an excluding mint, the service reads the `permissions` GitHub
      returns. If an excluded permission is present, or the reply names none, it revokes the token (best effort) and
      throws `InvalidOperationException`, and nothing is cached.
      `E0_a_minted_token_that_still_carries_an_excluded_permission_is_revoked_and_refused` covers both cases (Theory):
      one DELETE `installation/token` with the leaked token, and an empty cache.
    - **Every option is copied (owner: "yes" to more tests):**
      `SparkBuilderExtensionsTests.AddGithubWebhooks_copies_every_settable_option_found_by_reflection` sets each public
      settable property, so a new option fails until it is copied. The hand-listed propagation test beside it missed
      this bug as well.
    - **The tests can fail:** the last two WireMock tests were written alongside the code. To check them, all three
      spots were broken at once: the option copy was removed, the reply check was skipped, and a failed read fell back
      to the whole grant. Exactly the 4 expected tests failed, each for the expected reason ("found
      {"ExcludedTokenPermissions"}", and "to throw … but no exception was thrown" three times). The code was then
      reverted, and 23/23 passed.
    - Results: 14/14 `GitHubInstallationServiceWireMockTests` pass, and 1/1 `InstallationTokenScopeTests` passes.

    **Classic protection: move, then delete (owner, 2026-11-06, chosen over "edit it in place" and "only report
    it").** Once the rulesets exist, the confirmed plan carries classic protection's settings into them and then
    deletes the classic protection (`DELETE /branches/{b}/protection`), so the rules live in one place:
    - its required checks go to ruleset 2, pre-filled for the person to confirm (e3);
    - its review options go to ruleset 2's `pull_request` rule (code owners, conversation resolution), and so do
      linear history and signed commits;
    - "no force pushes" and "no deletions" are covered by ruleset 1's `non_fast_forward` and `deletion`;
    - its review count is replaced by the count asked for in the plan.
    A classic setting with no ruleset equivalent, such as push restrictions to named people, teams or Apps, stops the
    plan with a message instead of being dropped. A required check the person removed is listed as dropped in the
    deletion step. The order is ruleset 1, then ruleset 2, then the deletion, so the branch is never unprotected in
    between. Because the deletion needs ruleset 2 to receive the checks, e1's pure planner covers both rulesets. e3
    keeps the prompt: the checks to confirm and the approval count.

    **e1 built (2026-11-06, `1013f7f60` + `7d92c9665`):**
    - **Planner:** `MergeQueueRulesetPlanner` (Library, `Forge/MergeQueueRulesets.cs`) is pure.
      - Ruleset 1 holds exactly `update`, `deletion` and `non_fast_forward`. The App is `exempt`, and an `always`
        is repaired. The admin pull-request bypass is kept as it is unless the person chooses.
      - Ruleset 2 keeps people's bypass entries, its other rules and GitHub's own parameters, and drops the App.
      - The plan stops on: push restrictions; the queue's own check being required (D62); duplicate names; an
        approval count outside 0–10; a plan without rulesets.
      - Tests: `MergeQueueRulesetPlannerTests` (10).
    - **GitHub adapter:** `IForgeMergeQueue.GetRulesetStateAsync` and `ApplyRulesetPlanAsync` (other forges: none,
      or `NotSupported`), in `GitHubForgeMergeQueue.Rulesets.cs`. Tests: `GitHubForgeMergeQueueRulesetTests` (6) and
      the conformance list.
    - **Deviation from design item 4: the read also uses a scoped token, with `administration: read`.** The reason:
      classic protection's details (`GET /branches/{b}/protection`) answer 403 without Administration, which is why
      `GetBranchRulesAsync` reads `ChecksOnly` today. A ruleset's `bypass_actors`, which the plan compares, are read
      with the permission that may change them.
    - **A refused step throws, with the steps before it done.** Planning again picks up where it stopped, because
      the planner is idempotent.

    **Rulesets apply at the merge (owner, 2026-11-06: "At the merge (Recommended)", chosen over applying them
    when the person confirms).** The chooser asks the questions; the answers are stored in the onboarding state
    (`VersioningOnboardingState.Rulesets`); `MergedAsync` plans again on the then-current state and applies. A
    blocker or a refused step is commented on the onboarding pull request, with the way back (run the chooser
    again, or merge-queue.md §3). A plan with no files (the queue's workflow is there already) applies at once.

    **e2/e3 built (2026-11-06):**
    - **Prompt (e3):** with the merge queue picked, a second retry form (the virtual `MergeQueueRulesets` type:
      approvals, required checks, admins may merge by pull request) starts from ruleset 2, else classic protection,
      else the checks the default branch's head reports and one approval (`MergeQueueRulesets.Defaults`). The
      queue's own check is never offered (D62). "Skip" leaves the rulesets out. A forge that refuses the read shows
      the refusal in the confirmation instead.
    - **Confirmation (e2):** the plan's steps, or its blockers, are listed under the summary
      (`MergeQueueRulesets.Describe`). With release lines, the rulesets cover `~DEFAULT_BRANCH` and the line pattern
      (`{…}` → `*`).
    - Tests: `MergeQueueRulesetPlannerTests` +4 (defaults from each source, the description), `OnboardingServiceTests`
      +4 (`E2_*`, `E3_*`: applied after the merge on the default branch and the lines; a blocker commented on the
      pull request; applied at once without files; the prompt's checks from the head). 39/39 pass. Break-it check:
      with the at-merge apply, the comment and the head fallback broken, exactly the 5 new behaviour tests failed;
      reverted.
    - **The form's (i) hints (owner: "add Descriptions/tooltips, so that the (i) button appears").** The
      translations already held a description for every SetUpFeatures and MergeQueueRulesets attribute, yet no (i)
      showed. Cause: a retry prompt's form is drawn from the object's attributes alone (its virtual type usually has
      no read grant, so the types list does not carry it), and `PersistentObjectAttribute` had no `Description`. Fixed
      in Spark itself, so every retry form gains its hints: `PersistentObjectAttribute.Description` (C#, written
      only when set, so other payloads are unchanged; TS), copied by `EntityMapper.FromDefinition`, and kept by the
      retry modal's `attrToDefinition`. Tests: `EntityMapperFactoryTests` (the scaffold carries it),
      `PersistentObjectAttributeTests.The_description_travels_on_the_wire_both_ways`, and the modal spec "keeps each
      attribute description". 25/25 and 10/10 pass. They were written before the code. The C# ones cannot compile
      without the new member; the modal spec was not run red (without the mapping, `description` is undefined).

    **e5 built (2026-11-06):** ng-bootstrap#451 ran a whole pass and was evicted at the merge ("At least 1 approving
    review is required by reviewers with write access. Cannot change this locked branch").
    - **Reading the lock.** Classic protection's details (reviews, `lock_branch`) answer 403 without Administration,
      and no ordinary token holds it (e0). So `GetBranchRulesAsync` now reads `GET …/branches/{b}/protection` on a
      scoped `administration: read` token, revoked when done. It mints only when the branch is classically protected,
      and only at enqueue (the method's one caller). An installation that cannot mint one reads as `ChecksOnly`, as
      before. `ForgeBranchRules.LockBranch` is new; it defaults to false, so the other forges are unchanged.
    - **Refused at enqueue:**
      - `locked`: "Lock branch" is on.
      - `reviews`: the branch requires more approvals than the pull request has. GitHub counts each reviewer's latest
        word when it is an approval from someone with write access other than the author.
      - **My choice, not in #554's text:** the count is the strictest of the rulesets and classic protection
        (`RequiredApprovals`), not classic protection's alone. A ruleset's count refuses the merge just the same.
      - Both refusals name the setting and point to **Set up features**.
      - A `ChecksOnly` branch gets a warning that its lock and review count were not checked.
    - **Eviction hint:** `MergeRefusalHint` recognises "locked branch" and "approving review" in the forge's refusal and
      names those settings. The old advice (most recent push, stale approvals) stays for any other refusal.
    - Tests:
      - `MergeQueueEngineTests`: 4 `E5_*` tests.
      - `GitHubForgeMergeQueueTests`: `E5_classic_protection_is_read_on_an_administration_read_token_and_its_lock_is_seen`.
        The checks-only case now fails the mint instead (`StubGitHub.ScopedThrows`).
      - 247/247 pass.
      - Break-it check: with the lock read, both refusals, the warning and the hint disabled, exactly the 5 new
        behaviour tests failed (242 passed). Reverted.

    **e6 built (2026-11-06):** the docs and the in-app guides.
    - **merge-queue.md:**
      - §2: the chooser asks for the rulesets and applies them at the merge.
      - §2.1: the Administration row, and how the scoped tokens use it.
      - §3: "Set up by the chooser", and ruleset 1's JSON with `deletion` and `non_fast_forward`, as the chooser
        creates it.
      - §5: the `locked` and `reviews` rows, and the `merge-refused` hint.
    - **onboarding.md:** the ruleset form, the on-merge step 5, the permission.
    - **hosting.md:** Administration, held only by scoped tokens.
    - **In-app guides:**
      - `/onboarding/setup`: the on-merge step, and the by-hand item now applies only when the rulesets were skipped
        or failed.
      - `/merge-queue/setup`: the chooser does both rulesets. The suggested ruleset names are now the ones the
        planner finds them by. The old guide said "any name you like", and a ruleset by another name would have been
        created a second time. The ruleset 1 snippet also gained `deletion` and `non_fast_forward`.
    - Specs: `onboarding-setup` and `merge-queue-setup`, 19/19 pass.
    - **e4 (organization rulesets) is dropped.** The MintPlayer organization is on GitHub Free, and organization
      rulesets need Team or Enterprise. The owner then narrowed it to a per-repository batch on the Account page
      ("Per-repo batch only"), and dropped that too in favour of the redesign below.

    **#554 redesign: a one-shot OAuth app token (owner, 2026-11-06).** This supersedes every Administration-based
    part above.
    - **Why Administration came off the Apps.** GitHub Support confirmed that no GitHub App permission narrower than
      Administration covers rulesets. It advised: "the safer pattern today is to avoid granting that permission to
      the app ... keep ruleset management with a human account". The owner said: "It's not okay to implement this".
      The changelog of 2026-10-13 ("GitHub App improvements for least privilege") adds no ruleset permission either.
    - **Removed and checked (2026-11-06).** Repository Administration was removed from CoverageDevelopment and
      CoverageProduction, and each now shows 8 repository permissions instead of 9. A removal needs no acceptance:
      all four installations (the owner's account and MintPlayer, for each App) list no Administration, and none has
      a pending request.
    - **The design** (owner: "(1) sign in (2) get a token from my oauth app (3) configure the rulesets for all
      selected repos (4) revoke the token --- using a spark CustomAction"; then "Go with the new OAuth app"):
      1. **The admin runs one Spark custom action on several selected repositories.**
      2. **The action opens a popup to authorize a dedicated GitHub OAuth app** with the `repo` scope. It uses PKCE
         S256, its `state` is protected, and the popup is not a sign-in.
      3. **The callback exchanges the code for a token** and holds it only in a one-time, in-memory handoff tied to
         the user, for 5 minutes at most.
      4. **The action applies the rulesets to each repository in turn,** with a planner and adapter as e1's,
         rebuilt in f2 on this branch.
      5. **It revokes the token in a `finally`**, with `DELETE /applications/{client_id}/token` and Basic auth
         (client id and secret).
      The server never stores the token, and the App's private key can no longer mint anything that changes
      rulesets.
    - **Why not the existing GitHub Apps' own sign-in.** Without the private key, the App's user token is still
      capped at the App's permissions. GitHub's docs on acting on behalf of a user say: "The app can only access
      resources that it has permission to access" and "A token cannot grant additional access capabilities to a
      user". Granting Administration so that sign-ins get it would also hand it to installation tokens.
    - **Why a new OAuth app.** It is neither SeoTester's nor MusicDemons's, the organization's two existing OAuth
      apps. SeoTester is production's sign-in, and a leaked secret there would also reach this flow. MusicDemons
      would put another product's name on the consent screen. A fine-grained personal access token pasted by the
      admin would have been narrower (Administration on the selected repositories only, with a 1-day expiry), but the
      owner chose the one-click popup.
    - **Accepted costs:**
      - The `repo` scope is "full control of private repositories" for as long as the token lives.
      - An organization that restricts OAuth apps must approve this app once. New organizations restrict them by
        default ("OAuth app access restrictions are enabled by default"), MintPlayer included. "API access to
        private organization resources is not available for unapproved OAuth apps."
      - A compromised server receives the tokens of the admins who run the step while it is compromised. A stolen
        file gets nothing.
    - **What the spikes found (2026-11-06; three read-only agents, docs and code, not called live):**
      - **Confirmed from docs:**
        - `DELETE /applications/{client_id}/token` uses Basic `client_id:client_secret` with body
          `{access_token}`, and answers 204. `…/grant` instead removes all of the user's tokens for the app.
        - Scopes are set per authorization request, so only this step asks for `repo`.
        - PKCE is S256 only.
        - Device flow needs no secret, but revocation needs one anyway: not used.
      - **Confirmed from code:**
        - The retry pump with a client method, as `AddPasskeyAction.cs:53` does with `Retry.Invoke("webauthn.create")`.
        - The external-login popup round-trip (`spark-auth.service.ts:335-442`, `ExternalLoginPopupHtml`) and its
          nonce rules (`SparkExternalLoginNonce`).
        - The link callback cannot be reused: it stores the tokens in RavenDB and refreshes the sign-in
          (`LinkExternalLoginCallback.cs:51,66-77`).
        - The adapter takes only `client.Connection.Credentials.Password` from its scoped client and works on the raw
          token from then on (`GitHubForgeMergeQueue.Rulesets.cs:37,84`), so a user token drops in. (That file is
          on the abandoned branch only; f2 rebuilds the adapter on a given token from the start.)
        - Multi-select query actions read `args.SelectedItems` (`"selectionRule": ">0"`, actions.json:23-35, as in
          `EnableDependencyUpdatesAction`).
      - **Unconfirmed:**
        - whether `public_repo` is enough for public repositories;
        - whether a user token may add an `Integration` bypass actor;
        - whether revocation takes effect at once;
        - whether GitHub Free enforces rulesets on private repositories (Enterprise Server 3.12's docs say public
          repositories only).

    **Plan for the redesign (f-milestones, same pull request, which closes #554):**
    - **f0, spikes, live.** Needs a test OAuth app and a throwaway public and private repository; the owner gives
      the go-ahead first, because it creates things on GitHub. Each spike ends with a recorded answer.
      - S-A: does a `public_repo` token create a ruleset on a public repository? If yes, a selection of public
        repositories only asks for `public_repo`.
      - S-B: a user token adds the App as `Integration` / `exempt`.
      - S-C: right after `DELETE /applications/{id}/token`, the token answers 401.
      - S-D: what an organization that has not approved the app answers (status and message), and the approval
        link to show.
      - S-E: GitHub Free with a private repository: is creating a ruleset accepted, refused, or accepted but not
        enforced? Is it visible through the API?
      - S-F: does a second authorization with `repo` show the consent page again, or pass silently?
    - **f1, a one-shot OAuth token in Spark.Authorization** (generic, no GitHub-specific code):
      - `IOneShotOAuth` takes a named client (authorize URL, token URL, revoke callback) and scopes. Inside a custom
        action it returns an `IAsyncDisposable` token lease that revokes on dispose.
      - Pass 1: `Retry.Invoke("oauth.authorize", …)` returns the authorize URL, carrying a PKCE challenge and a
        protected `state` that holds a handle.
      - The callback endpoint requires a signed-in user and never signs in. It checks that the handle belongs to the
        current user, exchanges the code, puts the token in the handoff store and serves the popup page with its own
        message type.
      - Pass 2: the action removes the token from the store (single use) and gets the lease.
      - The store is `IMemoryCache` (one node; the VPS runs one), with a 5-minute expiry.
      - ng-spark/auth: an `oauth.authorize` client method, reusing the popup logic. A closed popup answers Cancel.
      - Tests: handle bound to its user, single use, expiry, `state` tampering, revoke on dispose and on throw.

      **f1 built (2026-11-06, `feat/issue554-rulesets-oauth`):**
      - **API:**
        - `ISparkOneShotOAuth.AuthorizeAsync(client, scopes)` returns a `SparkOneShotOAuthOutcome`: an
          `ISparkOneShotOAuthLease` (`AccessToken`, `GrantedScope`; disposing it revokes), or an error from
          `SparkOneShotOAuthErrors` (cancelled, popup_blocked, denied, expired, failed).
        - It is registered by `spark.AddOneShotOAuthClient(name, client => …)`. That needs
          `AddAuthentication<TUser>` first, and also maps `GET /spark/auth/oauth-callback`, but only while a client is
          registered.
        - Named `ISparkOneShotOAuth` rather than the plan's `IOneShotOAuth`, after Spark's `ISpark*` services.
      - **Deviation: the store is not `IMemoryCache`.** It is a `ConcurrentDictionary` with one timer per attempt,
        `SparkOneShotOAuthStore`. Two reasons:
        - An OAuth app token does not expire on its own. A user who consents and then closes the tab never sends
          pass 2, and `IMemoryCache` evicts lazily, so it might never call back to revoke. The timer removes the
          attempt after 5 minutes and revokes any token in it.
        - `TryRemove` makes the single take atomic.
        - A token that arrives after its attempt is gone, or that a wrong user tried to take, is revoked at once.
      - **State:** the protected `state` carries the handle and the popup nonce, so an expired attempt can still
        report to its popup. The callback is claimed once (`TryClaimCallback`), so a replayed redirect exchanges
        nothing.
      - **Deviation: the popup page shares the sign-in's message type and channels.** The plan asked for its own
        message type. The server-issued nonce tells the two apart, and the message carries no token, only
        success or error. So a mix-up can only settle the wrong wait, never leak anything. The page's texts are
        its own (`PopupTexts.Authorize`).
      - **Popup closing:** a closed popup does not answer Cancel, against the plan. Under COOP a popup on
        github.com reads closed while the user is still consenting, as with sign-in. Instead, the client method
        rejects (Cancel) after `SPARK_ONE_SHOT_OAUTH_TIMEOUT_MS`, which is 5 minutes.
      - **Service worker:** CodeCoverage's `ngsw-config.json` already excludes `/spark/**` from navigation, so the
        callback's `redirect_uri` needs no `ngsw-bypass`. Another app with a service worker needs that exclusion
        too.
      - **Client:** `SparkAuthService.authorizeInPopup(url, nonce)` shares the popup wait (`awaitPopupHandoff`)
        with sign-in. Only sign-in supersedes other attempts, drives `externalLoginPending`, and refreshes CSRF and
        the user. The `oauth.authorize` client method opens only http(s) URLs. It answers `{ handle }`, or
        `{ error: 'popup_blocked' }`, and rejects on timeout.
      - **Risk for f0/E2E:** `window.open` runs after the 449 round trip, so the click's user activation may have
        expired and the popup may be blocked. If so, the action reports `popup_blocked`.
      - **Tests:** `SparkOneShotOAuthTests`, covering the URL and PKCE, the round trip, single use, the wrong user
        at the callback and at the take, a tampered state, denied, a failed exchange, expiry before the callback, a
        token nobody takes being revoked by the timer, revoke on dispose, on throw, and once only, and a failing
        revocation not throwing.

    - **f2, the forge API on a given token (rebuilt, the abandoned e1 as reference):**
      - A pure planner, as e1's `MergeQueueRulesetPlanner`. Ruleset 1 holds `update`, `deletion` and
        `non_fast_forward`, with the App `exempt`. Ruleset 2 holds the checks and the `pull_request` rule. The
        classic-protection move and the plan's blockers follow the e1 notes above.
      - `IForgeMergeQueue.GetRulesetStateAsync` and `ApplyRulesetPlanAsync` take the user's token from the start;
        other forges answer `NotSupported`. `AppIdAsync` stays on the App JWT. No `administration` installation
        token exists anywhere: there is nothing to drop, since e0 is not on this branch.
      - Enqueue keeps master's behaviour: classic protection reads as `ChecksOnly`. Whether to bring back e5's
        refusals is decided in f2. A ruleset's approval count is readable with Metadata (`GET …/rules/branches/{b}`);
        a classic lock is not.

      **f2 built (2026-11-06):**
      - **Planner:** e1's `MergeQueueRulesetPlanner` was cherry-picked unchanged (commit `eed93cdc1`), with
        `MergeQueueRulesetPlannerTests`.
      - **Adapter:** both methods of `GitHubForgeMergeQueue.Rulesets.cs` take `accessToken` and send every repository
        request with it, through `SendAsync(token, …)` and `GetPagedAsync(…, token)`. `AdministrationClientAsync`
        and the scoped, revoked installation token are gone. The adapter never revokes anything: the caller's lease
        does. `AppIdAsync` still asks `GET /app` with the App's JWT.
      - **Refusals:** a 401/403 is a `ForgeAccessDeniedException` with GitHub's message and no `RequiredPermission`.
        Accepting an App permission cannot help, since the person lacks admin or the organization has not approved the
        OAuth app.
      - **e5's enqueue refusals are not brought back here.** Enqueue runs on the installation token, which now has no
        Administration and so cannot read classic protection's details. The ruleset half (Metadata) would be
        readable. Master's `ChecksOnly` behaviour stays. Reopening this is the owner's call.
      - **Tests:** `GitHubForgeMergeQueueRulesetTests` (6) checks four things:
        - every repository request carries the given token, and no installation client is created;
        - apply orders the creates before the delete, and calls no `/installation/token`;
        - a 403 keeps GitHub's message;
        - a blocked or empty plan sends nothing.
        `ForgeIntegrationConformanceTests` passes a token. The three classes ran: 31 passed.
    - **f3, the "Set up rulesets" action on a repositories query, with multi-select:**
      - The form's (i) hints need `PersistentObjectAttribute.Description` again (the abandoned `80a3d1e60`). It is
        cherry-picked or rebuilt here.
      - Registering the client in CodeCoverage maps `GET /spark/auth/oauth-callback`. That endpoint goes into
        CodeCoverage's route snapshot and `CsrfSurfaceTests`. It is a GET, and its `state` is the CSRF defence.
      - `CheckAsync` checks every selected repository.
      - One form asks for the approvals and whether admins may merge by pull request; each repository's checks
        default from its own head (`MergeQueueRulesets.Defaults`).
      - ~~One confirmation lists every repository's plan or blockers. The token is minted only after confirmation.
        Then: authorize, apply one repository at a time, revoke in `finally`.~~ **Superseded (owner, 2026-11-06:
        "Popup first, then preview").** Order: form → popup → read every repository with the token → one
        confirmation with each repository's exact plan or blockers → apply one at a time → revoke.
        - **Why the old order cannot work.** GitHub's "Permissions required for GitHub Apps" lists
          `GET /repos/{o}/{r}/branches/{b}/protection` under Administration (read), which the Apps no longer have. The
          ruleset reads are Metadata, but "Get a repository ruleset" returns `bypass_actors` "only … if the user making
          the API request has write access to the ruleset", and the planner needs them. So only the user's token can
          read a full plan.
        - **f1 changed for it.** The lease survives a prompt. `IRetryAccessor.IsWaiting` (new) is true while a pass
          unwinds for an unanswered step. The lease's dispose then gives the attempt back (`Borrow`/`Return`), and
          revokes otherwise. One pass holds it at a time, and the 5-minute lifetime still revokes it regardless. Tests:
          `A_prompt_after_authorizing_keeps_the_token_for_the_next_pass`,
          `A_conversation_left_at_its_prompt_is_revoked_when_its_five_minutes_end`,
          `A_lease_disposed_after_its_five_minutes_does_not_revoke_twice`.
        - **Accepted cost:** the token lives while the admin reads the confirmation, 5 minutes at most from pass 1.
          Rejected: two popups (authorize twice), and no exact preview.
      - **f3 built (2026-11-06).**
        - **Commits:** `e1e7763e0` (attribute descriptions, cherry-picked from 80a3d1e60), `0449b299e` (the lease
          that survives a prompt), and the f3 commit after them.
        - **Evidence:** `SetUpRulesetsActionTests` (6), `MergeQueueRulesetPlannerTests`, `RouteTableSnapshotTests` and
          `AnonymousSurfaceTests` gave 20 passed, 0 failed. The route snapshot gained exactly one line,
          `GET /spark/auth/oauth-callback | authorize(...)`. `securityPosture.txt` is unchanged, because the new grant
          goes to group `…0001`, not to anonymous callers. `modelHashes.json` was regenerated with
          `--spark-synchronize-model`.
        - **Contents:**
          - `CustomActions/SetUpRulesetsAction.cs`.
          - `MergeQueue/GitHubRulesetsOAuth.cs` (client, revoke via `DELETE /applications/{id}/token`, `IsConfigured`).
          - `Program.cs` registers it always, with keys `GitHub:{env}:RulesetsOAuth:ClientId`/`:ClientSecret`.
          - `App_Data`: `Model/SetUpRulesets.json`, the `SetUpRulesets` action (query, `>0`), the grant `…00ac`, and the
            translations (`rulesets.*`, model, action).
          - `RepositoryActions.OnDisableActionsAsync` disables the action outside `Account_Repositories` and
            `GetRepositories`.
          - `MergeQueueRulesetPlanner.Defaults`/`Describe` (from e3).
          - `CodeCoverage.Tests/CustomActions/SetUpRulesetsActionTests.cs` (6 tests).
        - **RESUME HERE: f6 and f0 (both the owner's).** f4 and f5 are built, see below.
          - f6: **apps registered (2026-11-06, owner's request, via Playwright)** under the MintPlayer org:
            - "CoverageRulesets Development", app 3998060, client id `Ov23liwK8RNgonrIgR0N`, callback
              `https://localhost:5200/spark/auth/oauth-callback`.
            - "CoverageRulesets Production", app 3998061, client id `Ov23liMQyURxiLowEn1R`, callback
              `https://coverage.mintplayer.com/spark/auth/oauth-callback`.
            - Both have device flow off and **"Expire user access tokens" off**. That GitHub option (on by default)
              would also issue a long-lived refresh token, which the lease never uses or revokes.
            - The production client id is in the VPS `.env` (backup `.env.bak-20261106-rulesets`), and the
              development one is in user-secrets.
            - **The secrets are the owner's** (owner, 2026-11-06: "You generate them"). **Both are set** (owner,
              2026-11-06): the user-secret, and the VPS `.env`. The `.env` was checked by count only: one non-empty
              secret line, 0 CRLF lines.
            - The VPS values take effect only after this branch is deployed, because the live compose file doesn't
              map them yet.
          - f0: the live spikes, for which the owner gives the go-ahead.
          - After that come the final test run (including the ng-spark retry-modal spec and the two guide specs), the
            push and the PR, all when asked.
        - **`CsrfSurfaceTests` needs no entry:** it covers mutating controller actions only, and the callback is a
          library GET.
        - **Known gap:** the apply pass re-reads and re-plans. A ruleset changed by someone else between the
          confirmation and the apply is planned afresh, not refused.
        - **Not committed on purpose:** `file.component.html` `mb-2` → `m-2` (owner's request, unrelated to #554).
      - The report covers each repository. An unapproved organization gets the approval link (S-D). A private
        repository on Free gets S-E's answer.
      - Which query it sits on (the Account's repositories, or the onboarding rows) is decided in f3.
    - **f4, the chooser:**
      - The at-merge apply of e2/e3 is dropped: no user is present at the merge to authorize.
      - **Decided (owner, 2026-11-06): apply at confirmation.** The chooser's ruleset step applies at confirmation
        time with the same one-shot token, so one click covers it. The chooser does not point to the action instead.
        The rulesets do not depend on the onboarding pull request's files.
        - **Accepted trap:** the rulesets go live before that pull request merges, so the queue check is required
          before the workflow that reports it exists.
      - **Built (2026-11-06):**
        - **Shared steps:** `MergeQueue/MergeQueueRulesetSetup.cs` (`IMergeQueueRulesetSetup`) holds them: target,
          popup, read, form, summary, apply, notify. "Set up rulesets" and the chooser both use it, so the steps are not
          written twice.
        - **The chooser with the merge queue picked:**
          - After the checkbox form come the popup and the rulesets form.
          - The confirmation adds "Rulesets, set up now …" with the plan.
          - The rulesets apply after `OnboardAsync`, and only when the onboarding was not refused. A refused one leaves
            no workflow to report ruleset 2's checks.
        - **When the rulesets can't be set up** (not GitHub, no default branch, no OAuth app, a refused read), the
          confirmation says "left to set by hand" with the reason, and the pull request still opens.
        - **Cancelling** the popup or the rulesets form ends the chooser.
        - **Release lines get no ruleset step:** the planner covers the default branch only.
        - **Tests:** `SetUpFeaturesRulesetsTests` (4).
    - **f5, docs and guides:**
      - merge-queue.md §2.1/§3/§5: no Administration, the OAuth app, the org approval.
      - onboarding.md, and hosting.md (new config keys `GitHub:{Development|Production}:RulesetsOAuth:ClientId` and
        `:ClientSecret`, and the OAuth app's callback).
      - The in-app guides.
      - **Built (2026-11-06):**
        - merge-queue.md: §2 (the chooser and the action), §2.1 (admin rights only through the one-shot token), and
          §3's new "Setting them up from CodeCoverage". §3 covers the steps, the org approval, and that release lines
          are by hand.
        - onboarding.md "Left to you".
        - hosting.md: §2 has the `.env` row, §4 the `GitHub:{Env}:RulesetsOAuth:*` row with both callbacks, and the
          permissions text.
        - `docker-compose.yml` maps `GITHUB_RULESETS_OAUTH_CLIENT_ID`/`_SECRET` (optional, empty by default), and
          `.env.example` lists them.
        - The guides: `/onboarding/setup` (by hand only when the chooser could not), and `/merge-queue/setup` (a
          paragraph at ruleset 1 pointing to both ways).
    - **f6, the owner registers the two OAuth apps** ("CoverageRulesets Development" with a localhost callback,
      "CoverageRulesets Production" with coverage.mintplayer.com) and sets their secrets. Device flow stays off.
  - **M10f:** the guide page and the docs. **Built (2026-11-06):** the in-app guide `/onboarding/setup` ("Getting
    started", first under Guides at order 0, so no other unit moved; `onboarding-setup.component.spec.ts`), the
    reference `docs/code-coverage/onboarding.md` (indexed in its README), and versioning.md and dependency-updates.md
    pointing to the chooser. The #153 PRD's "Set up versioning" is marked superseded.
  - Then the M12 leftovers, S7 (live, after the deploy), and the local sweep. **M12 leftovers done (2026-11-06):**
    backports.md (line renames, the line workflow pull request) and merge-queue.md §2 (the chooser; rulesets by hand
    until #554); the dependency parts landed with M9c.
- **M9 Dependencies on lines** (D13): a rolling PR per supported line, platform packages capped at the line's major,
  patch/security default, advisories outside the allowed range flagged. Pack verified by S4 (D10b; no upper bounds,
  opt-in `major-bounded`). **Built (2026-11-05):** every supported line, the current one included (owner's choice). It
  has a run document, bot branches and a pull request per line, scheduled from the default run's `FileLines`.
  `dependencies.lines` (patch/all/security/off) sets the ceiling, never a major, and a security fix needing a major is
  held. User docs are in dependency-updates.md §4; decisions in PRD §8.4.
- **M10 Onboarding chooser** (D3, S8), including the branch rename PR → rename on merge (D2, S7) and adding the
  regenerate workflow to each line (D9). The rulesets (#554, extended to release lines; PRD §8.2) follow in #554's
  own pull request (owner, 2026-11-06):
  plan shown first, applied with the signed-in user's GitHub token, a sign-in popup when that token is missing or
  expired.
- **M11 Spark itself.**
  - Workflow triggers for `release/**`; `schema-revision`, baseline and deploys default-branch-only (D18).
  - The `release/**` ruleset.
  - Cut `release/dotnet-11.x` and `release/angular-22.x` (current, D15), `release/dotnet-10.x` at `61c5c4276^` and
    `release/angular-21.x` at `1b5d7450e^` (D14), with seed tags.
- **M12 Docs** (`docs/code-coverage/backports.md`, versioning.md, merge-queue.md), then the local test sweep, one push,
  CI. **#570's part done (2026-11-05):** the backport section moved from versioning.md into `backports.md` unchanged,
  with who may ask and what the report holds added; versioning.md keeps a pointer. #571 adds the dependency and
  onboarding parts.
- **After merge, deploy, then roll out** (C2) to MintPlayer's library repos one by one via the onboarding chooser.
