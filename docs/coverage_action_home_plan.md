# Plan — move the coverage-upload action beside its API

**Implements:** [`coverage_action_home_PRD.md`](coverage_action_home_PRD.md) Option 3 ·
**Status:** implemented in this repository (M0 handed off, M5 partial, M6-M7 pending consumers)

All code lands as **one unit of work** per `CLAUDE.md` ("One pull request. Never split work."). The
cross-repo pieces are sequenced, not split: M0 must be on `github-actions/main` before M2 can run
green, and M7 must come last. Everything in `MintPlayer.Spark` is a single PR.

## Sequencing constraints

```
M0 (github-actions: compile-ts-action + PR CI)
     └─> M1 (Spark: restore self-contained action/) ──> M2 (Spark: wire build/verify)
             └─> M3 (deploy path filter)  └─> M4 (capabilities + contract)  └─> M5 (dogfood gate)
                                                                                └─> M6 (tag + repoint consumers)
                                                                                        └─> M7 (delete from github-actions)
                                                                                                └─> M8 (docs)
```

M0 is in another repository and must merge first — pointing at `compile-ts-action@main` before it
exists resolves to nothing. Archived refs keep resolving throughout, so **no milestone opens an
outage window** for the five frozen consumers.

---

## M0 — `compile-ts-action` in `MintPlayer/github-actions`

**Status: HANDED OFF, not implemented here.** A session rooted in this repository cannot write to
another repository, so the complete files — `action.yml`, `publish.sh`, the new `pull-request.yml`
and the converted `publish.yml` — are packaged verbatim in
[`code-coverage/compile-ts-action-handoff.md`](code-coverage/compile-ts-action-handoff.md), to be
applied from a session rooted in `github-actions`.

**Consequence:** `pull-request.yml` here already references
`MintPlayer/github-actions/compile-ts-action@main`, so its `coverage-action` job **fails until that
handoff lands**. Deliberate: a temporary pin would need a second corrective commit, which is how the
previous move left five repositories pinned to an archived repo. Coverage uploads are unaffected —
they use `uses: ./apps/CodeCoverage/action`, which needs no external repository.

**Inputs**, as actually specified (smaller than first drafted — `entry` and `minify` were dropped,
because `build-command` already names its own entry point and ncc's flags belong in the package's own
`build` script, not in a second place that can disagree with it):

| Input | Default | Why |
|---|---|---|
| `working-directory` | `.` | The self-contained action folder |
| `output-dir` | `dist` | Checked in verify mode, committed in push mode |
| `node-version` | `20.x` | **Not 18.x.** `publish.yml` pins 18.x while `coverage-upload` declares `using: node20` — an existing mismatch worth not inheriting |
| `install-command` | `npm ci` | `publish.yml` uses `npm install`, so its lockfile is not enforced. `npm ci` is the correct default for a folder that ships a committed bundle |
| `build-command` | `npm run build` | |
| `test-command` | `npm test` | **Currently never run in CI anywhere.** Must default to running |
| `mode` | `verify` | `verify` = rebuild and fail on drift; `push` = rebuild, commit, push |
| `commit-message` | `Pack with dependencies to ${output-dir}` | |
| `version-tag-from` | *(empty)* | Path to a `package.json` whose `version` mints an immutable tag. Created, never moved: **fails** if it already exists on a different commit |
| `version-tag-prefix` | `v` | `coverage-upload-v` → `coverage-upload-v1.2.0` |
| `major-tag` | *(empty)* | Moving tag, e.g. `coverage-upload-v1`. Force-updated to the commit just pushed |
| `token` | `${{ github.token }}` | |

Both tag inputs are explicit opt-in. Do **not** inherit `publish.yml`'s accidental
`tags: true, force: true` on the push step — that force-pushes *every* tag in the repo as a side
effect of pushing the bundle. Tag movement must be a deliberate, named operation.

The moving-major half is not new work: `publish.yml` already contains a commented-out
**"Update Major Tag"** step that did exactly this. Un-comment it, generalise it into this action, and
give it the immutable counterpart it never had.

**Steps:** `setup-node` → install → test → build → then branch on `mode`.

- `verify`: fail if the tree is dirty, using the archived repo's proven gate — but with
  `git status --porcelain <output-dir>`, **not** `git diff --name-only <output-dir>`. The latter is
  the PRD's R3 bug: it misses untracked files, so a first-ever build silently commits nothing.
- `push`: commit `<output-dir>` as `github-actions[bot]`, push the branch **without** `--force` (a
  non-fast-forward means someone pushed while we built, and failing is the right answer), then move
  the tags — force applied to the moving major tag alone.

Also in M0, because `compile-ts-action` cannot otherwise be trusted:

- Add the **missing `pull_request` workflow** to `github-actions`. It has none — the jest suite never
  runs and dist staleness is never checked before merge.
- That workflow calls `compile-ts-action` with **`mode: verify`** for every action in the repo, not
  just `coverage-upload`. All five remaining actions gain a test run and a staleness gate they have
  never had, and the verify path gets exercised on every PR rather than only at release time.
- Convert `publish.yml` to call `compile-ts-action` with `mode: push` for its six actions, proving
  the extraction against the existing pipeline.

The two modes are deliberately the same code path with one branch at the end, so a green `verify` on
a PR is genuine evidence that the subsequent `push` will produce the same bytes.

**Verify:** at least one untouched action (`delay` is the smallest) rebuilds to a **byte-identical**
`dist/index.js`. That is PRD exit criterion 4 and the only real proof the extraction is faithful.

---

## M1 — restore `apps/CodeCoverage/action/`, self-contained

The two source trees are **byte-identical** today except the entry point: the archived
`main.ts` has `async function run()` + a trailing `run();`, while `github-actions` has
`export async function run()` plus a 5-line `src/coverage-upload/index.ts` that calls it. Keep the
**exported** form — it is strictly better, since tests can import without executing.

Files to create in `apps/CodeCoverage/action/`:

| File | Source |
|---|---|
| `action.yml` | from `github-actions/coverage-upload/action.yml`, with `main:` changed from `../dist/coverage-upload/index.js` to **`dist/index.js`** (PRD R7) |
| `src/{main,context,credential,files,status}.ts` | from `github-actions/src/coverage-upload/` |
| `src/*.test.ts` | ditto, **converted jest → vitest**. `test-stubs.ts` is **deleted**: it existed only to emulate `vi.stubEnv`/`vi.stubGlobal`, which vitest provides — its own header said so |
| `src/capabilities.ts` + `capabilities.test.ts` | new, M4's client half |
| `src/bundle.test.ts` | new, M5's built half — drives the committed bundle |
| `package.json` | new; deps `@actions/{core,exec,github,glob}`, devDeps `@vercel/ncc ^0.38.4`, `typescript ^5.9.0`, `vitest` + `@vitest/coverage-v8`, `github-action-ts-run-api` |
| `package-lock.json` | generated here — the folder must build from itself alone |
| `tsconfig.json` | ES2022 / commonjs / `rootDir: src`, `types: ['node','vitest/globals']` |
| `vitest.config.ts` | v8 provider, `all: true`, `include: ['src/**/*.ts']`, `exclude: ['src/**/*.test.ts','dist/**']` — recoverable verbatim from the archived repo |
| `dist/index.js` | **committed in this same PR** (PRD R3) |
| `README.md` | restores the target of two currently-broken links |

Build script: **`ncc build src/index.ts -o dist --minify`** — `index.ts`, not `main.ts`. `main.ts`
only *exports* `run()`; bundling it produces an action that defines the function and never calls it,
exits 0, and uploads nothing. This was hit for real during implementation and is now pinned by
`bundle.test.ts`. Keep `--minify` (smaller commits — the
archived un-minified bundle was 2,379,564 bytes) and **drop `--license licenses.txt`**. Note
`--minify` is why ncc had to be bumped in `github-actions` commit `ef3d065` ("bundle did not parse —
`--minify` breaking private fields"); pin `^0.38.4` or newer.

**Do not add this folder to the root npm workspaces.** Re-affirmed decision, PRD §3.5 /
`coverage_monorepo_plan.md:299-304`: CJS node20, own tsconfig, `typescript ^5.9.0` vs the repo's
`6.0.3`, private lockfile.

**Testing: vitest** (PRD R5). The tests were vitest in the archived repo and were converted to
jest only during adoption into `github-actions`, so this reverts to their original form and aligns
with the repo's own vitest 4.1.11 — while the private lockfile keeps the version independent. The
conversion is mechanical (`vi` for `jest`, and the config file is recoverable verbatim from the
archived repo), and it deletes `jest.config.js` + `ts-jest` along with the `d.ts`-breaks-resolution
footgun documented in that config.

`nx affected` will not pick this folder up — it is outside the npm workspaces by design — so M2
wires it explicitly rather than relying on the workspace test sweep.

---

## M2 — wire the build into this repo's workflows

Two call sites, each ~2 lines of `uses:` plus inputs:

1. **`.github/workflows/pull-request.yml`** — new job, `mode: verify`. Runs the 35 jest tests and
   fails on a stale bundle. This is the gate `github-actions` never had.
2. **`.github/workflows/coverage-action-publish.yml`** — its own file, `mode: push`, path-filtered
   to `apps/CodeCoverage/action/**`, plus both tag inputs from M6 — **both derived from the action's
   `package.json` version**, never hardcoded, so a major bump retires the old moving tag without a
   workflow edit. It also takes a `workflow_dispatch` `bump` input (`none`/`patch`/`minor`/`major`)
   that performs the version bump itself, which is the intended way to cut a release; documented in
   [`apps/CodeCoverage/action/README.md`](../apps/CodeCoverage/action/README.md#releasing-a-new-version).

   The separate file is not tidiness: GitHub has no per-job `paths:` filter, and `major-tag` is
   force-moved on every run. Putting this job in `dotnet-build-master.yml` — which is what the first
   implementation did — fires it on every master push and walks `coverage-upload-v1` onto unrelated
   commits, so the tag comes to mean *"latest master commit"* rather than *"latest commit carrying a
   v1-compatible bundle"*. The immutable/moving split only means something if the moving tag moves
   for a reason. Caught while reviewing
   [github-actions#8](https://github.com/MintPlayer/github-actions/pull/8), whose `publish.sh` moves
   the major tag unconditionally — correctly, given the workflow is supposed to be filtered.

`master` is **unprotected** (verified) so the push-back needs no special handling, and
`GITHUB_TOKEN` pushes do not re-trigger workflows. Record in a comment that swapping in a PAT would
require `[skip ci]` (PRD R2).

---

## M3 — stop action changes from deploying the server

`.github/workflows/code-coverage-deploy.yml:11-39` triggers on `apps/CodeCoverage/**`. Once the
action lives under that path, **every action-only commit would build an image and SSH-deploy to the
VPS.** The archived repo guarded exactly this with `paths-ignore: ['action/**']`.

Add `- '!apps/CodeCoverage/action/**'` to the `paths:` filter. Verify by pushing an action-only
change to a branch and confirming the deploy workflow does not queue (PRD exit criterion 7).

---

## M4 — the capabilities endpoint and the contract rules

Server, `apps/CodeCoverage/CodeCoverage/Controllers/UploadsController.cs`:

- Add `GET /api/uploads/capabilities` → `{contract: 1, features: [...]}`. Auth consistent with the
  other three endpoints (`ApiToken,GitHubOidc` + `[SparkAuthorize("Upload","Coverage")]`); reuse the
  `uploads-status` rate-limit policy, not `uploads`.
- Codify PRD C2-C4 as a comment block beside the existing promise at :177-180, and in
  [`upload-api.md`](code-coverage/upload-api.md) beside the `state` closed-set rule at :130-139.

Action side:

- Read capabilities once; **treat 404 as contract 0** — that is what the deployed image returns
  today, so an unmodified old server is self-describing.
- Extend the tolerate-absence rule from `state` to `baseline`, `projection`, `patch`,
  `baselineScope`.

Tests: a server-side test asserting the endpoint's shape, and an action-side test asserting graceful
degradation on a 404.

---

## M5 — the dogfood gate — PARTIALLY DELIVERED

Two halves, and only one of them is built.

**Built: the bundle is exercised end-to-end.** `apps/CodeCoverage/action/src/bundle.test.ts` (10
cases, `npm run test:bundle`, wired into `pull-request.yml`) spawns the **committed
`dist/index.js`** as a child process against a real HTTP server speaking the documented contract,
asserting: it actually runs and uploads; it probes capabilities first; a 404 capabilities response is
a quiet success; `partial` against a server lacking it produces a warning; reports are gzipped and
round-trip to the original bytes; `finish` is called; and `fail-ci-if-error` decides both ways.

This catches the class of bug that is invisible to every `src/`-level test — and it caught a real one
during implementation: the bundle was first built from `src/main.ts`, which only *exports* `run()`,
producing an action that exits 0 having done nothing. See PRD R3.

**Also built:** `uses: ./apps/CodeCoverage/action` is now what both workflows here upload with, so
every PR uploads real coverage using the action as that PR changes it.

**Not built: a live server in CI.** Standing the real app up in a workflow needs RavenDB, a
`github-app.pem` and `Coverage:BaseUrl` matching the OIDC audience (`Program.cs:207-218`) — none of
which could be verified from this session, and a workflow written blind against them would be worse
than none. The stub server pins the action's side of the contract; `UploadsControllerCapabilitiesTests`
pins the server's. What remains unproven is the two meeting over a socket.

Remaining work, for whoever picks it up: a job with a RavenDB service container that runs the app
with a `covt_` token (`pull-request.yml` has no `id-token: write`, so the token path is simpler than
OIDC), then `uses: ./apps/CodeCoverage/action` against it. PRD exit criterion 5 — prove it goes red
by breaking one `UploadForm` field — applies to that job, not to the stub.

---

## M6 — publish a tag, then repoint every consumer

Consumers must not pin `@master` of a monorepo whose default branch moves constantly for unrelated
reasons.

**Tag scheme — two levels, only one of which moves:**

| Tag | Moves? | Points at | Who uses it |
|---|---|---|---|
| `coverage-upload-v1.2` | **Never.** Created once, on one commit. | The exact commit whose `dist/` was built | Anyone who needs a frozen, auditable pin |
| `coverage-upload-v1` | **Yes**, force-updated on each relevant push | The latest commit carrying a v1-compatible bundle | All consumers, by default |

Consumers pin the **moving major**, `coverage-upload-v1`, so a bundle fix reaches all six repos with
no PR anywhere. The immutable minor exists so any given upload is reproducible after the fact, and so
a consumer that gets bitten can pin backwards without forking.

The `coverage-upload-` prefix is deliberate. Spark currently has **no release tags at all** (one
local `backup/…` tag, zero on the remote), so a bare `v1`/`v1.2` is available — but the framework
publishes at 10.x (NuGet) and 22.x (npm), and a bare `v1` in a monorepo's tag namespace stops being
self-explanatory the moment anything else in the repo wants a tag. A hyphen rather than a slash
(`coverage-upload/v1`) avoids relying on slash-in-ref support in `uses:` syntax, which is the one
part of that grammar not worth betting a cross-repo cutover on.

The `contract` integer from M4 is a **separate** axis from this tag. A contract bump is a server-side
compatibility statement; the major tag tracks the action's own input/output surface. They will not
move together and should not be conflated.

Then, one-time (owner has confirmed this is acceptable):

| Repo | Files | New ref |
|---|---|---|
| `MintPlayer.Spark` | `pull-request.yml:156`, `dotnet-build-master.yml:123` | `MintPlayer/MintPlayer.Spark/apps/CodeCoverage/action@coverage-upload-v1` (but see M5 — this repo's own PR job uses `./apps/CodeCoverage/action`) |
| `MintPlayer.AspNetCore.SpaServices` | `pull-request.yml`, `build-master.yml` | ditto |
| `MintPlayer.Dotnet.Tools` | `pull-request.yml`, `dotnet-build-master.yml` | ditto |
| `MintPlayer.AI` | `pull-request.yml`, `build-master.yml` | ditto |
| `MintPlayer.AspNetCore.Tools` | `pull-request.yml`, `publish-release.yml` | ditto |
| `mintplayer-ng-bootstrap` | `pull-request.yml:125`, `publish-master.yml:96` | ditto |

All 15 inputs and 26 outputs keep their names, so every call site is a one-line substitution —
`url:`, `token:`, `flags:`, `partial:`, `base-sha:` arguments stay exactly as they are.

**Also fix the source of new bad refs:** `repo-setup-panel.component.ts:60` in this repo emits
`uses: MintPlayer/CodeCoverage/action@master` to every repo onboarded through the UI. It must emit
the new ref. This is arguably the most urgent single line in the whole plan.

Editing the component **is** sufficient, but only because the built SPA is not committed:
`apps/**/ClientApp/dist/` is gitignored (`.gitignore:396`) and `git ls-files` returns nothing under
it. A local `chunk-*.js` containing the old string is a build artifact on one machine, not repository
content. The image builds the SPA itself (`nx run @spark-apps/code-coverage:build` inside the
Dockerfile), so the corrected snippet reaches users on the next deploy — which does still depend on
the unmet precondition in [`old-repo-decommission.md`](code-coverage/old-repo-decommission.md) that
`code-coverage-deploy.yml` succeed at least once.

`self-coverage-PRD.md:225` already records this inconsistency as a known open item, and
`build-log-m0-m10.md:66` independently confirms the original tooling ("node20 + TypeScript + ncc
bundle, dist/ committed + CI staleness check") — including that a separate `coverage-action` repo was
considered and dropped.

This supersedes [`ng-bootstrap-action-path.md`](code-coverage/ng-bootstrap-action-path.md), whose
pending instruction (repoint at `github-actions/coverage-upload@main`) becomes wrong. Mark that doc
superseded rather than deleting it — it explains the intermediate state.

---

## M7 — remove the action from `github-actions`

Only after M6 is fully landed. Delete `coverage-upload/`, `src/coverage-upload/`,
`dist/coverage-upload/`, its `pack:coverage-upload` script and its entry in `all`. Leave a README
pointer to `MintPlayer.Spark/apps/CodeCoverage/action`. `compile-ts-action` stays — it now serves
five actions there and one here.

---

## M8 — documentation

- Stale `MintPlayer/CodeCoverage/action@master` refs: `product-overview.md:39,207,220`,
  `self-coverage-PRD.md:44,225`.
- Broken links now repairable because `action/README.md` exists again:
  [`upload-api.md:14`](code-coverage/upload-api.md), `apps/CodeCoverage/README.md:45`.
- `apps/CodeCoverage/README.md:28,48,179` still credit `.github/workflows/ci.yml` and `publish.yml`
  — files that exist only in the archived repo. The real workflows are `pull-request.yml`,
  `dotnet-build-master.yml`, `code-coverage-deploy.yml`.
- `product-overview.md:203-226` describes the action's design as if the folder were here; it becomes
  true again. **Done:** its "pin `@master` for now; a `v1` tag is cut once the input surface settles"
  line is replaced by the two-tag scheme and a pointer to the release flow, and
  `self-coverage-PRD.md`'s matching deferral is marked superseded. The release flow is documented in
  three places a reader might start from: the action's own README (canonical),
  `apps/CodeCoverage/README.md`, and the `docs/code-coverage/` index.
- [`old-repo-decommission.md`](code-coverage/old-repo-decommission.md) precondition 2 currently
  requires the action to be live in `github-actions`; rewrite against M6. Its precondition 3 (one
  successful deploy from this repo) still stands and is **still unmet** — `code-coverage-deploy.yml`
  has never run.
- Reverse the D2 decision record in [`coverage_monorepo_PRD.md:80`](coverage_monorepo_PRD.md), citing
  this PRD. Do not silently contradict it.

---

## Verification sweep (run once, at the end)

Per `CLAUDE.md`, test suites are batched — no per-milestone runs. The final sweep:

1. `npm test` in `apps/CodeCoverage/action` — **50 tests** (`vitest`), then `npm run test:bundle`
   — **10 more** against the built bundle.
2. `npm run build` there; `git status --porcelain dist` must be clean.
3. `dotnet test` for `CodeCoverage.Tests` — covers the new capabilities endpoint.
4. The dogfood job green, and red on a deliberately broken field.
5. `grep -rn "MintPlayer/CodeCoverage/action" .` → nothing.
6. An action-only push does not queue `code-coverage-deploy.yml`.
7. `delay`'s `dist/index.js` byte-identical after the M0 extraction.
8. `coverage-upload-v1` resolves from a consumer repo, and `coverage-upload-v1.2` points at the same
   commit it was cut on after a subsequent push has moved the major.

## Decisions — settled 2026-09-01

| # | Decision | Consequence |
|---|---|---|
| 1 | **vitest**, not jest | M1 converts the 35 tests back to their original form; `jest.config.js` + `ts-jest` are dropped |
| 2 | **Two-level tags**: immutable `coverage-upload-v1.2` never moves, `coverage-upload-v1` tracks the latest relevant commit | M0 gains `version-tag` + `major-tag` inputs; consumers pin the moving major (M6) |
| 3 | **Drop `--license licenses.txt`** — archived is archived | M1 build script is `ncc build src/main.ts -o dist --minify`; no `licenses.txt` travels |
| 4 | **Add `mode: verify` for all actions** in `github-actions`' new `pull_request` workflow | M0 scope covers the other five actions, which have never had a test run or a staleness gate |
