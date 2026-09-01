# Self-coverage, branch-scoped trends, and the accounts card

**Status**: implemented on `coverage-self-reporting`
**Branch**: `coverage-self-reporting`

Coverage analyses everyone else's repositories and has, since 2026-08-13, been in
production at `coverage.mintplayer.com`. Six MintPlayer repositories now upload to
it. This repository does not — not really. It has half a .NET pipeline and nothing
at all for its ~2 000 lines of TypeScript.

This is the dogfooding gap. It matters beyond tidiness: the mixed-format, multi-flag,
multi-session path through the server is exercised today only by
`mintplayer-ng-bootstrap`. Running our own repo through it makes that path a
first-class, continuously-tested surface rather than one customer's edge case.

## Where we actually stand

Investigated 2026-08-27 across this repo, the six consuming repos, and the two
reference PRs (`MintPlayer.AspNetCore.Tools#28`, `MintPlayer.AI#45`).

**.NET — collected, uploaded, but off the org standard.**
`.github/workflows/ci.yml` runs `dotnet test … --collect:"XPlat Code Coverage"` and
uploads via `uses: ./action`. `Coverage.Tests` already references
`coverlet.collector 6.0.4`. What's missing against the pattern every other repo
follows: no `coverlet.runsettings` (so no exclusions, no format control), no
`--results-directory`, no "did a report actually get produced" guard, no
`disable-search: true`, no fork-PR guard, no `fail-ci-if-error: false`, and no
`base-sha` on pull requests. (`publish.yml` runs its own `dotnet test` without
coverage, which is correct and stays that way — see M1.)

**TypeScript — nothing.** `Coverage/ClientApp` has 34 `.ts` files, **zero**
`.spec.ts`, no test runner in `devDependencies`, and no `test` target in
`angular.json` (only `build` and `serve`). `action/` has 598 lines across five
source files, one npm script (`build`), and no test runner either. `docs/PRD.md`
already names Vitest as the intended stack; `.gitignore` appears to anticipate
`ClientApp/coverage/`, though that rule turns out to be dead — see M1.

**README** has no badges of any kind.

## What we will build

### Decisions, and why

**Keep `uses: ./apps/CodeCoverage/action`, do not switch to a published ref.**
Every consumer pins `@master`. This repo should not: `./action` uploads with the
action *as the pull request changes it*, so a regression in the uploader is caught
by the PR that introduces it rather than by the next consumer to update. This is the
one place where deviating from the org pattern is the point.

**Hardcode `url: https://coverage.mintplayer.com`; drop the `vars.COVERAGE_URL`
gate.** The instance is live and the URL doubles as the OIDC audience server-side,
so it cannot be "normalised". The `vars` gate currently also serves as the
fork-PR skip, which it does badly — repository variables *are* exposed to fork PRs
while `secrets.COVERAGE_TOKEN` is not, so a fork PR today attempts an upload that
cannot authenticate. Replaced by the explicit fork guard the other repos use.

**Token auth, not OIDC.** `COVERAGE_TOKEN` is already provisioned org-wide and this
repo's CI already uses it. OIDC would be a second thing to debug for no gain.

**Three upload sessions, one build, distinguished by `flags`.** `dotnet`, `angular`,
`action`. The server merges sessions under `(repository, commitSha, runId,
runAttempt)` with max semantics and reports per-flag totals — so this gives three
readable numbers *and* one headline. A single mixed upload (what `ng-bootstrap`
does) would give one opaque number. The action rejects an upload with zero files, so
there is no "finish-only" invocation: `finish: true` rides on the last of the three.

**lcov paths must be rebased. This is not optional.** The server resolves report
paths against `git ls-files` by longest suffix and *silently drops what is
ambiguous* — `ng-bootstrap` lost 22.3% of its files to this before it was found.
This repo has a verified collision on day one: `src/main.ts` exists in **both**
`action/src/main.ts` and `Coverage/ClientApp/src/main.ts`. Vitest emits `SF:` paths
relative to its own root, so both would arrive as `src/main.ts`, match two entries,
and vanish — along with every other same-named file. A rebase step runs before
upload.

**Coverage measures product code, not the bundle.** `action/dist/index.js` is a
2.4 MB committed `ncc` bundle; it is excluded, as are `node_modules`, spec files,
and generated `.g.cs`.

### Out of scope

Genuinely not being done, not deferred:

- **Coverage gating / branch protection.** Thresholds live server-side (service UI
  or a repo-root `coverage.yml` read from the base ref), never in workflow YAML.
  Configuring them is a service-side action, not a code change.
- **Raising coverage.** This lands the measurement and a seed suite that proves the
  pipeline end to end. Writing tests to a target number is separate work driven by
  what the resulting report shows.
- **A `v1` tag for the action.** No tags exist; consumers pin `@master` by design
  until the input surface settles. Unrelated to measuring ourselves.

## Also on this branch

Two things landed here that are not about self-coverage. Both are small, both are
in files this branch already touches, and the repo's rule is one pull request —
so they ride along rather than waiting.

### Issue #17 — "Coverage over time" mixes branches

`BrowseController.GetHistory` *can* filter by branch; nothing ever asks it to. The
panel's only call site (`po-detail-page.component.ts:41`) passes no `[branch]`, the
input defaults to `''`, and the server reads that as "every branch". Commits are
then ordered by time alone, so a feature branch's points interleave with the
default branch's and the line zig-zags between two unrelated populations. The badge
beside that chart has always been default-branch-scoped
(`BadgeController.LoadBranchCoverage`), which is the inconsistency users actually
see.

Fixed server-side, not in the template: the endpoint is the shared contract, and
only the server knows `DefaultBranch` reliably. An explicit `branch` still wins;
absent one, the repository's default branch is used.

`DefaultBranch` is null for repositories provisioned by an OIDC upload — it is only
ever written from a GitHub webhook payload — and filtering on null would render an
empty chart. Those repositories fall back to every branch, which is what
`BuildFinalizer` and `BaseResolver` already do in the same situation.

`GetSparklines` had the identical defect and is fixed with it. It cannot reuse one
branch name because it spans repositories, so the scoping is applied per repository
after the fetch; a repository with many feature-branch uploads may therefore
contribute fewer points, which is acceptable for a sparkline and better than a line
that mixes branches.

### The "Your accounts" card scrolls instead of wrapping

Reported: on small screens the text wraps, and the badges leave their container.

Worth recording, because this card has been changed for this exact class of problem
twice already on `authorization-hardening` — **neither commit is an ancestor of this
branch**, so the template here is the pre-fix one:

- `62943e1` replaced the card with a Spark `spark-sub-query` grid. Its own commit
  message records that this did not make it responsive, and it deleted the card and
  the Resync button along the way.
- `8fa21af` reverted that 37 minutes later and fixed the symptom with `flex-wrap`
  plus `text-nowrap` on the badges.

We are deliberately **not** re-applying `8fa21af` here: graceful wrapping is what
the current report objects to. The card gets horizontal scrolling instead — the
wrapper goes around the list only, so the header, spinner and install hint keep
reflowing rather than sitting behind a scrollbar, and `flex-nowrap` + `text-nowrap`
are what make the row exceed the container in the first place. One `::ng-deep` rule
gives the inner `<ul class="list-group">` `min-width: max-content`; without it the
rows overflow their own boxes and the borders stop at the container edge, which
reads as broken rather than scrollable.

The Spark-grid route stays closed until Spark#308 and Spark#309 publish. When they
do, the target is a `bs-card` we own wrapping `spark-sub-query [showCard]="false"`.

Same bug class, one line each: `<bs-table>` in `commit-files-panel` and
`account.component` now pass `[isResponsive]="true"`. The library ships the
`.table-responsive` wrapper and defaults it off; both call sites had left it off.

## Milestones

### M1 — .NET collection on the org standard

- Add `coverlet.runsettings` at the repo root: `Format=cobertura`,
  `IncludeTestAssembly=false`, `UseSourceLink=false`, `DeterministicReport=false`,
  `ExcludeByFile=**/*.g.cs,**/obj/**` (Spark generates model code).
- `ci.yml` Test step gains `--settings coverlet.runsettings --results-directory artifacts/coverage`.
- Add the "Assert tests actually ran" guard — `dotnet test` exits 0 and prints
  nothing when it finds no test project, so a silently empty run must fail loudly.
- `publish.yml` deliberately gets **no** coverage collection and no upload. Both
  workflows trigger on a master push, so `ci.yml` already produces a complete
  three-flag build for that commit; a second, .NET-only upload from `publish.yml`
  would land as a separate build for the same sha and make the badge flap between
  the full number and the .NET-only one depending on which run finalized last. Its
  test job stays a plain deploy gate.
- `publish.yml`'s test job does gain `RAVENDB_LICENSE`, which `ci.yml` has and it
  does not — today the gate that guards a production deploy runs the embedded
  server in restricted mode while CI runs it licensed, so the two disagree about
  what passes.
- `.gitignore`: `/artifacts/`. Not a root-level `coverage/` — found the hard way:
  git is case-insensitive on Windows, so that pattern also matches the `Coverage/`
  **project** folder and hides the entire application from `git status`. The same
  investigation showed the pre-existing `ClientApp/coverage/` rule was already dead:
  a pattern containing a slash is anchored to the repo root, and the real path is
  `Coverage/ClientApp/`. Both are now spelled in full from the root.

### M2 — `action/` tests and coverage

- devDeps `vitest` + `@vitest/coverage-v8`; `test` and `test:coverage` scripts.
- `vitest.config.ts`: v8 provider, `lcovonly` + `text-summary`, output to
  `action/coverage/`, excluding `dist/`.
- Specs against the pure, testable seams — `files.ts` (glob resolution, the
  explicit-then-fallback rule, ignore patterns), `context.ts` (PR head sha vs merge
  sha, branch resolution), `credential.ts` (OIDC re-mint margin, invalidate), and
  `status.ts` (state branching, 429 backoff, the single 401 retry).
- CI: the `action-dist-check` job runs `npm test` before the dist check.

### M3 — `ClientApp` tests and coverage

- devDeps `vitest`, `jsdom`, `@vitest/coverage-v8`; `tsconfig.spec.json`.
- `angular.json` gains an `architect.test` target on `@angular/build:unit-test`
  (Angular 22's native builder, already present in `node_modules`) with
  `coverage: true` and `coverageReporters: ["lcovonly", "text-summary"]`.
- `test` npm script; seed specs over the non-trivial front-end logic.
- CI: the `angular-build` job runs `npx ng test` after the build.
- `coverageInclude` spans `src/**/*.ts` so untested files count against the total.
  Measuring only what the specs import reported 64.6%; the honest figure is 7.2%.

### M4 — path rebasing and the unified upload

- `tools/rebase-lcov-paths.mjs`: rewrites `SF:` entries to repo-root-relative
  forward-slash paths, handling both absolute and root-relative inputs, and
  **verifies every rebased path names a tracked file**, failing the job otherwise —
  the silent-drop failure mode is exactly what this milestone exists to prevent, so
  it must not be able to recur unnoticed. Tested on node's built-in runner, which
  keeps a one-file tool from dragging in a third test framework.
- Producing jobs publish their reports as workflow artifacts.
- A `coverage-upload` job (`needs: [test, angular-build, action-dist-check]`)
  checks out, downloads all three, rebases the two lcov files, and invokes the
  action three times — `flags: dotnet`, `flags: angular`, `flags: action` — with
  `finish: true` on the last, `disable-search: true` throughout, `base-sha` on pull
  requests, and the fork guard.

### M5 — badges and documentation

- README badges directly under `# Coverage`: the coverage badge
  (`https://coverage.mintplayer.com/badge/MintPlayer/CodeCoverage.svg` →
  `/r/MintPlayer/CodeCoverage`) alongside the CI status badge.
- Reconcile the README's action reference with what CI actually does — the file
  currently advertises `MintPlayer/CodeCoverage/action@master` while CI uses
  `./action`; both are correct, for different audiences, and should say so.
- A short "How this repo measures itself" section covering the three flags and the
  rebasing requirement, so the next repo with a nested workspace finds it.

### M6 — issue #17 and the accounts card

- `GetHistory` resolves an effective branch (explicit → default → all) and filters
  on the already-indexed `Branch` term.
- `GetSparklines` scopes each repository to its own default branch.
- Three tests in `BrowseControllerTests`: default-branch scoping, the null-default
  fallback, and per-repository scoping for the sparklines.
- The accounts list gets an `overflow-x-auto` wrapper, `flex-nowrap`/`text-nowrap`
  rows, and a component stylesheet for the `min-width: max-content` rule.
- Both `bs-table` call sites opt into `[isResponsive]`.

## Verification

Per the batching rule, suites run once at the end, not per milestone; intermediate
milestones are verified by reading and type-checking. The closing sweep is
`dotnet test` with the runsettings, `npm test` in `action/`, `npx ng test` in
`ClientApp`, and the rebase script asserted against a fixture containing the
`src/main.ts` collision. The pipeline itself is only truly verified once the PR runs
— the badge turning from `unknown` to a number is the acceptance signal.
