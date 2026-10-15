# spark-regenerate

The consumer half of the CodeCoverage merge queue (`docs/coverage_merge_queue_PRD.md` §5.6). When the queue
rebases a pull request and resolves a conflict in a generated file by taking the base side
(`modelHashes.json`, `securityPosture.txt`), those files are stale. The queue dispatches the repository's
`postRebaseActionName` workflow on its `mq/<base>/pr-<n>` branch; that workflow calls this action, which
regenerates the files and returns them as a patch. The queue then validates the patch and pushes it.

## What it does

`action.yml` sets up .NET and runs [`regenerate.sh`](regenerate.sh), which:

1. Fails unless `git rev-parse HEAD` equals `expected_sha` exactly (a full SHA). A run that built a
   different commit than the one the queue prepared must not hand back a patch (D14).
2. Fails unless the working tree is clean, so the patch holds only what synchronize wrote.
3. Runs `dotnet build -c Debug -p:EnableSpaBuilder=false` for each project. The SPA switch belongs to
   `MintPlayer.AspNetCore.SpaServices`; synchronize reads only the .NET model, so the Angular client is
   never built (`skip-spa-build: false` turns that off).
4. For each project runs `dotnet run --project <p> --no-build -c Debug -- --spark-synchronize-model`, then
   `--spark-synchronize-security`. This is the same `dotnet run` form as this repository's verify steps
   (`pull-request.yml`), so the files it writes are the ones `--spark-verify-*` accepts. Any non-zero exit
   fails the run (1 = synchronize stopped on an error such as an unparsable model file, 2 = misconfigured).
5. Runs the whole synchronize a **second** time and fails if that changes anything: synchronize must be at
   a fixed point, or every queue run would produce a fresh diff.
6. Writes `git diff --binary` against `HEAD`, taken through a throwaway index so new files
   (`Model/<Entity>.json`) are included and the real index is not touched, and uploads it as the
   **`merge-queue-patch`** artifact (file `merge-queue.patch`).

**An empty patch is uploaded too.** A green run with an empty patch means "nothing to regenerate"; a failed
run, or a run without the artifact, means the queue evicts. That keeps "nothing changed" distinguishable
from "it broke".

The action never commits, pushes or reads a secret. The queue applies the patch only if every path is
within `coverage.yml`'s `regeneratedPaths` and the patch holds no mode change, symlink, submodule or
binary (T4); the verify checks then run on the final commit.

## Inputs

| Input | Default | |
|---|---|---|
| `expected_sha` | (required) | The full SHA the queue prepared; pass the dispatch input through. |
| `projects` | (required) | App `.csproj` paths, one per line. `#` comments and blank lines are ignored. |
| `dotnet-version` | `''` | Empty reads `global.json`. |
| `configuration` | `Debug` | Keep it equal to the verify step's configuration. |
| `skip-spa-build` | `true` | Passes `-p:EnableSpaBuilder=false`. |
| `extra-build-args` | `''` | Appended to every `dotnet build`. |
| `lockfiles` | `''` | Opt-in: ecosystems whose lockfiles to regenerate first (`npm`, `pnpm`, `yarn`, `nuget`), below. |
| `pr_number` | `''` | With `lockfiles`: the dispatch's `pr_number`, for the marker rebuild and two-sided provenance. |
| `token` | `${{ github.token }}` | With `lockfiles` and `pr_number`: fetches `refs/pull/<n>/head` of a private repository. |

Outputs: `changed` (`true`/`false`), `file-count`, `patch-path`.

With only `expected_sha` and `projects`, as before, the action behaves exactly as it did: the new inputs default to
empty, the fetch step is skipped, and `regenerate.sh` skips the lockfile step.

## Lockfiles

With `lockfiles:` set, `regenerate.sh` runs the sibling action's
[`lockfile-regenerate/lockfiles.sh`](../lockfile-regenerate/lockfiles.sh) after the clean-tree check and before the
build, and its changes go into the same patch (so both synchronize snapshots include them). Before anything else,
the action fetches the pull request's head with that folder's `fetch-pr-head.sh`. What is regenerated, the
three-way marker rebuild, the fixed point, the provenance check, the fallback and the token are documented in
[`lockfile-regenerate/README.md`](../lockfile-regenerate/README.md). The checkout needs `fetch-depth: 2`, and
`coverage.yml`'s `regeneratedPaths` must list the lockfiles, or the queue refuses the patch (T4).

A repository can instead give its lockfile rules a workflow of their own (`action: lockfiles-regenerate.yml` on the
`autoResolve` rule, O36) from [`templates/lockfiles-regenerate.yml`](../../../docs/code-coverage/templates/lockfiles-regenerate.yml).

## Security

This action **builds and runs the pull request's code**. The calling workflow must:

- declare `permissions: contents: read` and nothing more, and never `id-token: write`;
- reference no secrets and no environment;
- check out with `persist-credentials: false`;
- run on a GitHub-hosted runner, never a self-hosted one.

A dispatched run uses the workflow file **from the dispatched `mq/` branch**, not from the default branch
(D12). The queue therefore never dispatches it for a PR that changes `.github/**` (unless the repository's
bypass role lets a same-repository PR through; a fork's such change is refused before anything is pushed, O21 A,
O22), and checks the workflow's blob on the `mq/` commit against the default branch's before every dispatch,
which nothing bypasses. Restore uses public feeds only; a repository
that needs a private feed cannot use this action without giving untrusted code a credential, and should
not. With `lockfiles` and `pr_number`, the action's first step uses the job's own read-only token to fetch the pull
request's head, before any pull-request code runs and without writing it to disk; the workflow still names no
token and no secret (lockfile-regenerate README, "The pull request's head, and the token").

## Using it

Copy [`docs/code-coverage/templates/regenerate.yml`](../../../docs/code-coverage/templates/regenerate.yml)
to `.github/workflows/regenerate.yml` on the default branch, list the apps, and pin the action to a commit
SHA of this repository. This repository dogfoods it with the local path in
[`.github/workflows/regenerate.yml`](../../workflows/regenerate.yml).

Pushes to `mq/**` fire `push` workflows (D14), and the queue pushes there only to dispatch this workflow (D65).
Add `branches-ignore: ['mq/**']` to any `push:` trigger that would otherwise match them; a `push:` limited to
`branches: [master]` needs nothing. For a fork's PR this is enforced: before it pushes the fork's code to `mq/`,
the queue reads the repository's workflows and evicts the PR (`fork-unsafe-workflows`) while any `push:` or
`create:` trigger would run on that branch, the regenerate workflow breaks the rules above, or a
`workflow_run:` trigger follows it there.

## Running it locally

Every input is an environment variable, so the script runs outside Actions (Git Bash on Windows works):

```bash
EXPECTED_SHA="$(git rev-parse HEAD)" \
PROJECTS="apps/DemoApp/DemoApp/DemoApp.csproj" \
BUILD_ARGS="-p:EnableSpaBuilder=false" \
PATCH_PATH=/tmp/merge-queue.patch \
bash .github/actions/spark-regenerate/regenerate.sh
```

It needs a clean tree; a clean `git worktree add --detach` is the easy way. `SKIP_BUILD=true` reuses the
binaries already on disk.

## Verification (2026-10-15)

R6 of conflict resolution v2 (PRD §10.5, §10.7; O35 #2 and #3; O36). Run on Windows 11 under Git Bash (git 2.47.1,
Node 24.15.0, npm 11.12.1, pnpm 11.2.2, Yarn 1.22.22 through corepack, .NET SDK 11.0.100-rc.1), against throwaway
repositories in the session scratchpad, with the real public registries (npmjs, yarnpkg, nuget.org).

**The simulation.** `sim.sh <eco> <scenario>` builds, per run: a source repository with a first commit (`left-pad`),
a pull request branch (adds `is-number@7.0.0`), the base branch (adds `is-odd@3.0.1`, which pulls `is-number@6.0.0`),
and the queue's prepared squash on top of the base: `package.json` with both dependencies (keep-both), the lockfile
**the base side** (take-base). A bare `origin.git` holds `mq/master/pr-7` (the squash) and `refs/pull/7/head`, and
the working copy is cloned from it over `file://` with `--depth 2`, as the template checks out. Then
`fetch-pr-head.sh` (PR 7) and `run.sh` (`LOCKFILES=<eco>`) run exactly as the action runs them.

| Run | Result |
|---|---|
| npm `ok` | fetch deepened the shallow clone and found the merge base; the head verified against HEAD; diff3 rebuilt (2 regions); `npm install --package-lock-only` resolved the markers; second pass unchanged; provenance: 4 packages, all from base or head; patch: `package-lock.json` only. Exit 0. |
| npm `pin` (head pins `kind-of@6.0.0` under `^6.0.0`) | rebuild keeps **`kind-of@6.0.0`**; provenance passes (strict). Exit 0. |
| npm `pin-nopr` (fallback) | lock-only install on the base lockfile re-resolves to **`kind-of@6.0.3`**; provenance passes by host allowlist with a warning naming `kind-of@6.0.3 (registry.npmjs.org)`. Exit 0. This pair is the measured difference O35 #2 asked for. |
| npm `moved` (a commit pushed to the head after the queue prepared) | fetch succeeds; `merge-tree` check rejects the head (`late.txt` merges cleanly yet differs from HEAD); fallback; exit 0. |
| npm `extra` (the squash's `package.json` names `is-even`, which neither lockfile holds) | provenance **fails**: 5 packages (`is-even@1.0.0`, `is-odd@0.1.2`, `is-number@3.0.0`, `kind-of@3.2.2`, `is-buffer@1.1.6`) not in base or head. Exit 1. |
| npm `extra-nopr` | the same packages pass by host allowlist (all on `registry.npmjs.org`), with a warning listing 6. Exit 0: the fallback's documented weakness. |
| npm `nopr` | no fetch; lock-only install; host-allowlist provenance; exit 0. |
| pnpm `ok`, `pin`, `extra` | as npm: diff3 rebuilt (3 regions) and resolved by `pnpm install --lockfile-only`; `pin` keeps `kind-of@6.0.0`; `extra` fails provenance with the same 5 packages (host `default registry`). |
| Yarn 1 `ok`, `pin`, `extra` | diff3 rebuilt (1 region) and resolved by `yarn install`; `extra` fails provenance (host `registry.yarnpkg.com`). In `pin` the pull request's own `yarn.lock` already held `6.0.3` (Yarn 1 re-resolved the new range when the fixture was made), so it shows no pin difference. |
| NuGet `ok` (`sim-nuget.sh`: a `netstandard2.0` library with `RestorePackagesWithLockFile`; base adds `Newtonsoft.Json 13.0.3`, the PR `Newtonsoft.Json.Bson 1.0.2`; the squash takes the base `packages.lock.json`) | head verified; `dotnet restore -p:RestoreLockedMode=false` twice, unchanged; provenance: 4 packages from base or head; patch `Lib/packages.lock.json`. Exit 0. |
| NuGet `extra` (the squash also references `System.Memory`) | provenance **fails**: `System.Memory@4.5.5` and its 3 dependencies. Exit 1. |
| NuGet `nopr` | provenance **fails closed**: `Newtonsoft.Json.Bson@1.0.2 (NuGet feed)` is not in the base and NuGet has no host to allow. Exit 1. |

**`provenance.mjs` alone**, on the npm `ok` output: an edited `resolved` (`https://evil.example/is-number-7.0.0.tgz`)
fails in both modes (`is-number@7.0.0 (evil.example)`, exit 1); the unedited file passes (exit 0); missing
arguments exit 2.

**Against GitHub.** In a `--depth 2` clone of this repository's `master` (the squash of #515), `fetch-pr-head.sh`
with `PR_NUMBER=515` and no token fetched `refs/pull/515/head` anonymously in 11 s and gave head `dac474ab`, merge base
`6eb4aaf8` (the #514 commit #515 was rebased on). `lockfiles.sh` then **verified that head against the real squash**
with `merge-tree`. With `FETCH_TOKEN=not-a-real-token` the fetch was refused (the header is sent), the script warned
and took the fallback (exit 0), and `git config --local` held no `extraheader` afterwards.

**`spark-regenerate` wiring.** With a stub `dotnet` on `PATH` (prints its arguments, exits 0): `regenerate.sh` with
`LOCKFILES=npm` ran the lockfile step before the stub build and both synchronize passes, and the patch held the
regenerated `package-lock.json` (with `kind-of`); without `LOCKFILES` the same commit gave a 0-byte patch, as before.

**Static checks.** ShellCheck 0.11.0 (through the `shellcheck` npm package) over `lockfile-regenerate/*.sh` and
`regenerate.sh`: only SC2016 (info) on the intended literal Markdown backticks, now suppressed in `run.sh` (the
same line in `regenerate.sh` predates R6). The setup page's three copied snippets were checked with a node script
replicating `merge-queue-setup.snippets.spec.ts`: both workflow templates byte for byte, `coverage.yml` by keys and
values: all equal. `tsc --noEmit -p tsconfig.spec.json` for the ClientApp: no error in the changed files (only the
workspace's TS6059 rootDir noise). The vitest suites and `MergeQueueForkGuardTests` were **not** run (the owner
batches test runs).

**Not verified:** Yarn 2+ (Berry), the action on a real GitHub runner, a private repository's token fetch, and the
`corepack` fallback when pnpm or Yarn is absent from the runner.
