# PRD — where the coverage-upload action lives

**Status:** DELIVERED · **Date:** 2026-09-01 · **Repos touched:**
`MintPlayer/MintPlayer.Spark`, `MintPlayer/github-actions`, plus 5 consumer repos (one-time)

Implementation record: [`coverage_action_home_plan.md`](coverage_action_home_plan.md). Findings that
changed the design as it was built are folded into the sections below rather than left in pull
request descriptions — §5 C5, §7 R8-R9 and §8 are the ones that moved.

## 1. The question

The `coverage-upload` GitHub Action and the server it talks to (`apps/CodeCoverage`, deployed at
coverage.mintplayer.com) currently live in different repositories. That was a deliberate choice
during the monorepo absorption — [`coverage_monorepo_PRD.md:80`](coverage_monorepo_PRD.md) D2, on
the reasoning that *"the application belongs beside the framework it consumes, the action belongs
with the other actions"*. This PRD revisits it, because the split has a recurring cost that the
absorption did not price in.

**This is not a greenfield decision.** The move already happened
([github-actions#5](https://github.com/MintPlayer/github-actions/pull/5)), so Option 2 below is the
status quo and Option 1/3 are reversals.

## 2. Goals

| # | Goal |
|---|---|
| G1 | Do not duplicate the action's code. One logical home. Moving it is allowed. |
| G2 | Do not duplicate the TypeScript → single `index.js` build system. |
| G3 | Keep using TypeScript. |
| G4a | A deployed old image must not break a newer action. |
| G4b | Changing the upload API must not require a two-repo, two-PR, switch-back-and-forth dance. |

## 3. Measured starting state

### 3.1 The action, as it exists in `MintPlayer/github-actions`

Default branch is **`main`**, public, unprotected. Layout is one folder per action at the repo root,
each holding *only* `action.yml`; sources are pooled in `src/<action>/` and bundles in
`dist/<action>/index.js`, so `coverage-upload/action.yml` points *out of its own folder*:
`main: ../dist/coverage-upload/index.js`.

The build is **repo-wide, not per-action**. There is no per-action `package.json`, `tsconfig.json`
or lockfile. Two stages, `@vercel/ncc` 0.38.4:

```json
"build": "tsc",
"pack:coverage-upload": "ncc build lib/coverage-upload/index -o dist/coverage-upload --minify",
"all": "npm run build && npm run pack:delay && … && npm run pack:coverage-upload",
"test": "jest"
```

There is **no bundler config file at all** — ncc is driven purely by CLI flags. That is what makes
G2 cheap to satisfy: only an entry path, an output path and a `--minify` flag need parameterising.

`dist/` is committed back to `main` by `.github/workflows/publish.yml`, authenticated with the
default `GITHUB_TOKEN` (no SSH deploy key, no PAT — the SSH-keypair concern in the original framing
is indeed obsolete), author `GitHub Action <action@github.com>`, via
`ad-m/github-push-action@master` with `tags: true` and `force: true`.

Three defects in that repo, all inherited rather than intended:

- **No `pull_request` workflow exists at all.** `publish.yml` is the only workflow. The jest suite
  is therefore **never run in CI**, and bundle staleness is never checked before merge.
- The dist-staleness gate that the old repo had (`action-dist-check`, rebuild + fail if
  `git status --porcelain dist` is dirty) **was lost in the port**.
- The skip-when-unchanged check is `git diff --name-only dist`, an unstaged-vs-HEAD diff that works
  only because `dist/` is already tracked. On a **first-ever build of a new folder** the files are
  untracked, the diff is empty, and the commit is *silently skipped*.

Versioning is unmaintained: consumers all reference `@main`; tags `v1`, `v2`, `v3`,
`v1.0.4`–`v1.0.10` and one literally named `remove` exist, one release from 2023-11-20, and the
"Update Major Tag" step is commented out.

### 3.2 The old repo is archived, and five repos are still pinned to it

`MintPlayer/CodeCoverage` is **archived** (last commit 2026-08-29). The ref still *resolves*, so
uploads keep working — but the bundle there can never be rebuilt again. Still pinned at
`MintPlayer/CodeCoverage/action@master`:

| Repo | Workflows |
|---|---|
| `MintPlayer/MintPlayer.AspNetCore.SpaServices` | `pull-request.yml`, `build-master.yml` |
| `MintPlayer/MintPlayer.Dotnet.Tools` | `pull-request.yml`, `dotnet-build-master.yml` |
| `MintPlayer/MintPlayer.AI` | `pull-request.yml`, `build-master.yml` |
| `MintPlayer/MintPlayer.AspNetCore.Tools` | `pull-request.yml`, `publish-release.yml` |
| `MintPlayer/mintplayer-ng-bootstrap` | `pull-request.yml`, `publish-master.yml` |

Only `MintPlayer.Spark` has migrated (`pull-request.yml:156`, `dotnet-build-master.yml:123` →
`MintPlayer/github-actions/coverage-upload@main`).

Worse, **the live server hands out the dead ref**: the SPA setup panel at
`apps/CodeCoverage/CodeCoverage/ClientApp/src/app/components/repo-setup-panel/repo-setup-panel.component.ts:60`
renders a copy-paste snippet containing `uses: MintPlayer/CodeCoverage/action@master`. Every new
repo onboarded through the UI is being pointed at an archived repository.

This is G4a in the mirror: those five repos are **frozen** against whatever API the stale bundle
speaks, and no amount of server-side care can un-freeze them without editing their workflows.

### 3.3 The API has no version, only a promise

All three endpoints are classic `[ApiController]` routes in
`apps/CodeCoverage/CodeCoverage/Controllers/UploadsController.cs` (`[Route("api/uploads")]`, :25):

| Endpoint | Line | Shape |
|---|---|---|
| `POST /api/uploads` | :47-149 | `multipart/form-data` → `UploadForm` (:353-371); **202** `{buildId, sessionId}`; 50 MB cap |
| `POST /api/uploads/finish` | :152-169 | JSON `{repository, commitSha, runId, runAttempt}` → **202** |
| `GET /api/uploads/status` | :182-245 | query params → `UploadStatusResponse` (:312-328) |

Auth is `[Authorize(AuthenticationSchemes = "ApiToken,GitHubOidc")]` + `[SparkAuthorize("Upload",
"Coverage")]`; unknown ≡ unauthorized ⇒ **404, never 403** (:409-410). OIDC audience is
`Coverage:BaseUrl` (`Program.cs:207-218`).

**There is no `api/v1`, no `ApiVersion`, no `api-version` header anywhere.** Compatibility is a
documentary promise — *"fields added, never removed or repurposed"* — stated three times
(`UploadsController.cs:177-180`, `apps/CodeCoverage/README.md:160-173`,
[`upload-api.md:3-4`](code-coverage/upload-api.md)).

One part of the contract is already correctly forward-compatible and must be preserved: `state` is
the only field a client may branch on, its set is closed (`InFlight | Complete |
CompleteWithErrors`), and **anything unrecognised is absorbed into `CompleteWithErrors`**
([`upload-api.md:130-139`](code-coverage/upload-api.md)).

### 3.4 Deployment is decoupled from the action by construction

`.github/workflows/code-coverage-deploy.yml` triggers on push to `master` filtered on
`apps/CodeCoverage/**` plus a hand-maintained list of 12 `libs/**` paths (:11-39), builds
`ghcr.io/mintplayer/codecoverage`, then SSHes to the VPS (`appleboy/ssh-action@v1.2.5`) which pulls
the image and runs an 18×10s `/health/ready` loop.

**This is the structural reason G4a can never be solved by co-location.** The action is consumed
from a *git ref*; the server ships as a *docker image* pulled by a VPS. Those two clocks are
independent even inside a single commit in a single repository. A newly-merged action is live for
consumers the instant it is pushed; the server it talks to is whatever the VPS last pulled.

### 3.5 This repo has no node-CJS bundling tooling

Root `package.json` has no `esbuild`, `@vercel/ncc`, `rollup`, `webpack`, `tsup` as a direct
dependency, and **no `build` script at all**. `@vercel/ncc` appears **0 times** in
`package-lock.json`. `@nx/esbuild` is absent; `nx.json:60-62` registers only `@nx/dotnet`. The only
TS build targets are `@nx/angular:application` — browser ESM, not node CJS.

So Option 1 in its naive form (rebuild the pipeline here) is a genuine from-scratch cost, exactly as
the original framing feared. And note the standing decision in
[`coverage_monorepo_plan.md:299-304`](coverage_monorepo_plan.md): the action was deliberately **not**
added to the root npm workspaces — CommonJS node20 bundle, its own `tsconfig`, `typescript ^5.9.0`
against this repo's `6.0.3`, private lockfile, committed `dist/` as contract. That decision stands
and this PRD does not disturb it.

## 4. Options

### Option 1 — action source in `MintPlayer.Spark`, build system duplicated here

Rejected. Violates G2 outright, and §3.5 shows the duplication is real work, not a copied config
file.

### Option 2 — status quo: everything in `MintPlayer/github-actions`

Satisfies G1, G2, G3. Fails G4b: every API change is two PRs in two repos with an ordering
constraint, and — the cost that is easy to miss — **the action can never be tested against the
server**. The old repo's `ci.yml` dogfooded with `uses: ./action` precisely so uploader regressions
failed in the PR. That is structurally impossible across repos, and its loss is unrecorded.

### Option 3 — RECOMMENDED — source beside the API, build system shared as an action

Two moves:

1. The `coverage-upload` **source** returns to `apps/CodeCoverage/action/`, restored to the
   self-contained shape the archived repo already had (own `package.json`, own `package-lock.json`,
   own `tsconfig.json`, ncc pointed straight at `src/main.ts`, `main: dist/index.js`).
2. The **build pipeline** is extracted once into a new composite action,
   `MintPlayer/github-actions/compile-ts-action`, with a `mode: verify | push` input. Both repos
   invoke it in ~2 lines. Nothing is duplicated: the coverage action's code lives only here, the
   build system lives only there.

This is feasible because the *original* layout was already self-contained — the
monorepo-ification happened during adoption into `github-actions` and is reversible. The four
`@actions/*` deps and a lockfile travel with the folder.

Scoring: G1 ✔ · G2 ✔ · G3 ✔ · G4b ✔ (one repo, one PR) · G4a — see §5, unchanged by any option.

**What Option 3 buys that Option 2 cannot:** `uses: ./apps/CodeCoverage/action` works again. An API
change and its action change are written, built, and *tested against a live local server* in the
same pull request.

**What Option 3 costs:**

- `github-actions` stops being "all actions in one repo". Accepted: the owner's framing explicitly
  permits moving code, and G1 forbids duplication, not distribution.
- Every consumer job downloads the Spark tarball to resolve the action. **Measured: 11.26 MiB pack,
  10.6 MB tree in 1,765 files.** Non-issue.
- A ~2.4 MB minified bundle is committed to `master` on each action change, growing the pack.
  Mitigated by `--minify` (the archived bundle was un-minified at 2,379,564 bytes).
- Consumers must be repointed. **Owner has confirmed this is acceptable — a one-time change.**

### Option 4 — keep it in `github-actions`, add a machine-readable contract instead

Not an alternative to Option 3 but a **component of it** (§5). Location decides how many PRs a
change costs; it never decides whether a running old image can serve a new action.

## 5. The compatibility contract (G4a) — required in every option

§3.4 establishes that action-vs-server skew is a property of the deployment topology, not of repo
layout. So this section applies whichever option is chosen, and is the *only* thing that actually
fixes problem (a).

**C1 — absence means baseline.** Add `GET /api/uploads/capabilities` returning
`{contract: <int>, features: [<string>]}`. The action treats **404 as contract 0**, which is exactly
what the currently-deployed image returns. An old image is therefore self-describing without being
modified.

**C2 — request fields stay additive by default.** ASP.NET model binding ignores unknown
`multipart/form-data` fields, so a new field sent to an old server is silently dropped rather than
rejected. New fields are therefore safe *provided the action does not depend on the server having
honoured them*. Any field whose absence changes correctness must be gated on C1.

**C3 — response fields stay optional.** The action must tolerate every response field being absent.
The existing closed-set-plus-absorb rule for `state` (§3.3) is the model; extend it explicitly to
`baseline`, `projection`, `patch`, `baselineScope`.

**C4 — breaking changes are forbidden without a bump and a window.** Removing a field, renaming
one, repurposing one, or requiring a new endpoint requires incrementing `contract` and keeping the
old behaviour for at least one deploy cycle. This turns the documentary promise of §3.3 into
something a reviewer and a test can check.

**C5 — the gate that makes C1-C4 real.** **DELIVERED**, though not as a CI job.

Specified as a workflow job hosting `apps/CodeCoverage` behind a RavenDB service container. That was
the wrong shape and the reason it shipped deferred: it needed secrets and startup prerequisites that
could not be verified while writing it. `CoverageRavenTest` already gives every test a live RavenDB
in CI, so the gate is an ordinary xUnit test —
`apps/CodeCoverage/CodeCoverage.Tests/UploadActionDogfoodTests.cs` — which starts the real
`CodeCoverage.dll` pointed at that server, seeds a `Repository` and an `ApiToken` through the store,
and drives `node dist/index.js` at it over a real socket through the full upload → finish → status
cycle. No service container, no secret, no new job, and it runs locally exactly as it runs in CI.

This is the dogfood gate the archived repo had (`uses: ./action` in its `ci.yml`) and the port lost.
Only Option 3 makes it possible, which remains the load-bearing argument for the whole decision.

**Two startup prerequisites, discovered by building it rather than assumed:**

- The app **refuses to start** without `GitHub:Production:ClientId` / `ClientSecret`. GitHub is the
  only sign-in provider it registers, so a missing client id means nobody could ever sign in and it
  says so loudly instead of starting half-configured. Placeholders suffice for ingestion, which
  authenticates with `ApiToken` / `GitHubOidc` and deliberately never accepts a browser cookie.
- It must run as **Production**. Under Development, `UseAngularCliServer` spawns the Angular dev
  server and fights for ports.

Both matter beyond this test: they are what anyone hosting the app outside the Docker image will hit.

**C6 — consumers pin a tag, not a moving branch.** Consumers must not pin `@master` of a framework
monorepo whose default branch moves many times a day for unrelated reasons. Two tag levels, only one
of which moves: **`coverage-upload-v1.2`** is cut once on one commit and never moves;
**`coverage-upload-v1`** is force-updated to the latest v1-compatible commit and is what consumers
pin. The moving major means a bundle fix reaches all six repos with no PR anywhere; the immutable
minor keeps any given upload reproducible after the fact and lets a bitten consumer pin backwards
without forking. Both are driven by the compile action as explicit, named inputs — never as a side
effect of pushing the bundle.

This axis is **independent of** the `contract` integer in C1: that is a server-side compatibility
statement, while the major tag tracks the action's own input/output surface. They will not move
together and must not be conflated.

## 6. Out of scope

- Un-archiving `MintPlayer/CodeCoverage`. It stays archived; only the refs pointing *at* it change.
- Renaming the `Coverage` RavenDB database or the `ghcr.io/mintplayer/codecoverage` image. Both are
  production identifiers — see `CLAUDE.md`.
- Migrating the 21 merged PRs / issue history out of the archived repo.
- Adding the action to the root npm workspaces. Explicitly re-affirmed against, §3.5.
- Introducing `api/v1` route prefixes. C1's capabilities document supersedes the need, and a route
  prefix would itself be a breaking change for the five frozen consumers.

## 7. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | `code-coverage-deploy.yml` triggers on `apps/CodeCoverage/**`, so an action-only change would **deploy the server**. The archived repo guarded this with `paths-ignore: ['action/**']`. | Add `!apps/CodeCoverage/action/**` to the path filter. Verify with a dry-run push. |
| R2 | The dist commit-back adds bot commits to `master`. `master` is **unprotected** (verified: `Branch not protected`, 404), so the push works — and `GITHUB_TOKEN` pushes do not trigger workflows, so no loop. | Keep `GITHUB_TOKEN`. If anyone ever swaps in a PAT, `[skip ci]` becomes mandatory — record this in the workflow comment. |
| R3 | The silent-skip bug of §3.1 fires on the **first** build in the new location, shipping an empty `dist/`. | Commit a built `dist/index.js` in the same PR that creates the folder; make the compile action use `git status --porcelain` (tracked *and* untracked), not `git diff`. |
| R4 | Repointing five repos is not atomic; a repo left on the archived ref keeps a frozen action. | Sequence per §5 C6: publish the tag first, repoint consumers, only then delete from `github-actions`. Archived refs keep resolving throughout, so there is no outage window. |
| R5 | jest↔vitest churn. `github-actions` converted the 35 tests vitest→jest; the folder's original home was vitest, and this repo standardises on vitest 4.1.11. | **Decided: vitest.** Reverts the tests to their original form and matches the repo, while the private lockfile keeps the version independent. Conversion is mechanical and the config is recoverable verbatim from the archived repo. |
| R6 | `compile-ts-action` is the first composite action in `github-actions`, which has no `pull_request` CI to validate it. | Add the missing `pull_request` workflow there as part of M0; it is a prerequisite, not a nicety. |
| R7 | The `main:` path must change from `../dist/coverage-upload/index.js` to `dist/index.js` when the folder de-pools. A wrong path fails at consumption time, not build time. | Covered by C5 — the dogfood job resolves `action.yml` and its `main:` for real. |
| R8 | **MATERIALISED.** `compile-ts-action`'s `token` input displaces the caller's git credential by clearing `http.<host>/.extraheader` from `--local` config. `actions/checkout@v4` stores it there; **`@v7`, which this repo standardises on, writes it to a separate included file** — so the clear matches nothing, the action's header becomes a *second* `Authorization`, and every git request 400s with `remote: Duplicate header`. The first publish run after merge failed exactly this way (run 33551373525). | Pass `token: ''` so the action defers to the checkout's credential, and keep the release-tag lookup on the REST API so it needs no git credential at all. Reported as [github-actions#9](https://github.com/MintPlayer/github-actions/issues/9); the workaround is removable once the action stops assuming `--local`. |
| R9 | **MATERIALISED.** A `push`-triggered workflow filtered on `paths:` never fires on a change to *itself*, so the publish machinery could only be validated by a later unrelated commit or a manual dispatch. #350 fixed a duplicate-`Authorization` bug in `coverage-action-publish.yml`, touched nothing else, therefore did not run, and sat unverified on `master`. | The workflow now lists its own path. A publish triggered by a workflow edit is harmless: bundle unchanged ⇒ nothing committed, release tag skipped as already published, moving tag re-points to the same tree. |

## 8. Exit criteria

1. `grep -rn "MintPlayer/CodeCoverage/action" .` returns nothing in this repo — including the SPA
   setup panel, `product-overview.md:39,207,220` and `self-coverage-PRD.md:44,225`.
2. ✅ All five consumer repos reference the tag from §5 C6 — ten workflow files, read from each
   default branch via the contents API.

   **`gh search code` is not a valid check for this.** Its index lagged by hours and reported all
   five as still stale, and separately reported this repo's own setup panel as stale after it was
   fixed. Worse, **GitHub redirects the old path**: `MintPlayer/CodeCoverage` now resolves to
   `MintPlayer-Archive/CodeCoverage`, so a straggler still pinning
   `MintPlayer/CodeCoverage/action@master` keeps working *silently* rather than failing loudly. That
   is precisely why the consumers had to be repointed deliberately instead of left to break.
3. `coverage-upload/` and `src/coverage-upload/` and `dist/coverage-upload/` are gone from
   `github-actions`, replaced by a README pointer.
4. `compile-ts-action` builds the remaining five actions in `github-actions` unchanged — verified by
   a byte-identical `dist/` for at least one of them — and `github-actions` has a `pull_request`
   workflow running `mode: verify` over **every** action there, so its test suite runs in CI for the
   first time.
5. ✅ The dogfood gate (C5) is green, **and demonstrably red when the API and the action disagree** —
   proven, not asserted: `[FromForm(Name = "commit_sha")]` on `UploadForm.CommitSha` produced
   `responded 400: {"errors":{"commit_sha":["The CommitSha field is required."]}}` and a failing
   test, then reverted. Its assertions are on numbers (`LinesCoverable == 2`, `LinesCovered == 1`)
   rather than presence, because a parser that drops a report whose paths it cannot match still
   produces a `Build` — an empty one.
6. `GET /api/uploads/capabilities` returns a contract integer; the action degrades correctly against
   a server that 404s it, proven against the currently-deployed image.
7. An action-only change does **not** trigger `code-coverage-deploy.yml` (R1).
8. Broken doc links repaired: [`upload-api.md:14`](code-coverage/upload-api.md) and
   `apps/CodeCoverage/README.md:45` both point at a `action/README.md` that exists again.
9. `apps/CodeCoverage/README.md` no longer references `.github/workflows/ci.yml` / `publish.yml`
   (:28, :48, :179) — files that do not exist in this repo.
