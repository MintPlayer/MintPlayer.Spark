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

### S6 — npm on an older major

- Does `MintPlayer/github-actions/publish-npm-packages` honour `publishConfig.tag`?
- Dry-run `v21-next` for an older-major prerelease.

### S7 — Renaming branches as an App (D2)

- Which App permission does `POST /repos/{o}/{r}/branches/{b}/rename` need for a non-default branch (Contents vs
  Administration)? Try it on the scratch repo with the dev App.
- Check what GitHub does with open PRs based on the old name, branch protection/rulesets targeting it, and the
  old-name redirect for `git fetch`.

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
- **M7 Merge queue for lines** (D12):
  - `mergeQueue.branches` patterns / `supported-lines`;
  - automatic enqueue of backport PRs;
  - the line's own regenerate workflow on its queue.
- **M8 Labels and reporting.**
  - Default labels (D5, the current line always, D16).
  - The "Backport targets" check run.
  - `backported: <line>` labels and the PR-body marker.
  - Recognising manual backport PRs and opening the PR on push (D7).
  - The cross-repo dependency diagnosis (D11).
- **M9 Dependencies on lines** (D13): a rolling PR per supported line, platform packages capped at the line's major,
  patch/security default, advisories outside the allowed range flagged. Pack verified by S4 (D10b; no upper bounds,
  opt-in `major-bounded`).
- **M10 Onboarding chooser** (D3, S8), including the branch rename PR → rename on merge (D2, S7) and adding the
  regenerate workflow to each line (D9). It also creates the rulesets (#554, extended to release lines; PRD §8.2):
  plan shown first, applied with the signed-in user's GitHub token, a sign-in popup when that token is missing or
  expired.
- **M11 Spark itself.**
  - Workflow triggers for `release/**`; `schema-revision`, baseline and deploys default-branch-only (D18).
  - The `release/**` ruleset.
  - Cut `release/dotnet-11.x` and `release/angular-22.x` (current, D15), `release/dotnet-10.x` at `61c5c4276^` and
    `release/angular-21.x` at `1b5d7450e^` (D14), with seed tags.
- **M12 Docs** (`docs/code-coverage/backports.md`, versioning.md, merge-queue.md), then the local test sweep, one push,
  CI.
- **After merge, deploy, then roll out** (C2) to MintPlayer's library repos one by one via the onboarding chooser.
