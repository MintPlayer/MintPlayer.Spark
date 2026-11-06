# Backporting to release lines (#568) — plan

Spec: [coverage_backport_PRD.md](coverage_backport_PRD.md). Tests run once per PR, at its end. Each PR runs its own
spikes first.

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
    - CoverageProduction and CoverageDevelopment hold no Administration today (installation page, 2026-11-06), so
      each installation must accept the new permission; until then the step refuses with the acceptance link (D95/O17).
    - e4 (organization rulesets) needs the organization Administration permission as well.
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
