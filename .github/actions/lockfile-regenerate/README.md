# lockfile-regenerate

The lockfile half of the CodeCoverage merge queue's regenerate step (`docs/coverage_merge_queue_PRD.md` §10.5,
O35, O36). When the queue rebases a pull request and resolves a lockfile conflict by taking the base side, the
pull request's dependency changes are gone from the lockfile. This action regenerates the lockfiles, keeping
both sides' pins where the package manager can merge them, checks where every resolved package came from, and
returns the result as a patch. The queue validates the patch and pushes it.

It is used two ways:

- **Standalone**, as its own workflow ([`templates/lockfiles-regenerate.yml`](../../../docs/code-coverage/templates/lockfiles-regenerate.yml)),
  named by `action: lockfiles-regenerate.yml` on the `coverage.yml` rules it serves (O36). It uploads its own
  `merge-queue-patch` artifact: every dispatched workflow returns exactly one.
- **Inside `spark-regenerate`**, through that action's `lockfiles:` input. `spark-regenerate` runs this folder's
  scripts before its build, and the lockfile changes travel in its one patch.

## Inputs

| Input | Default | |
|---|---|---|
| `expected_sha` | (required) | The full SHA the queue prepared; HEAD must be exactly this commit. |
| `lockfiles` | (required) | Ecosystems, one per line: `npm`, `pnpm`, `yarn`, `nuget`. Spaces and commas also separate. |
| `pr_number` | `''` | The dispatch's `pr_number`. Enables the marker rebuild and the two-sided provenance check. |
| `token` | `${{ github.token }}` | Only for fetching `refs/pull/<n>/head` of a private repository (below). `''` fetches anonymously. |
| `projects` | `''` | NuGet only: extra `.csproj` paths to restore. |
| `dotnet-version` | `''` | NuGet only: empty uses `global.json` when there is one, else the runner's SDK. |

Outputs: `changed`, `file-count`, `patch-path`, as `spark-regenerate`.

The checkout needs **`fetch-depth: 2`**: the work is derived from the prepared commit's own diff.

## What it does

1. **Fetches the pull request's head** ([`fetch-pr-head.sh`](fetch-pr-head.sh)), when `pr_number` is given, as the
   first step. It fetches `refs/pull/<n>/head` and deepens the shallow checkout (depth 50, 400, 3000, then the full
   history) until the head and the base branch (`HEAD^`) have a merge base. A failed fetch, or no merge base, is a
   warning and the fallback, never a failure.
2. **Checks that head** ([`lockfiles.sh`](lockfiles.sh)). The queue's squash records no pull request SHA, and the head
   may have moved since the queue prepared. So the head is used only if `git merge-tree --write-tree HEAD^ <head>`,
   the queue's own merge, gives HEAD's content on every path that merged without a conflict; conflicted paths are
   exactly where the queue chose a side or kept both. Any other head is the fallback.
3. **Derives the work** from `git diff --name-only HEAD^ HEAD` (the prepared squash) and, with a verified head,
   `git diff --name-only <merge base> <head>` (the pull request's own changes, which catches a lockfile-only change
   that take-base dropped entirely):
   - a changed `package.json` → the nearest `package-lock.json` / `npm-shrinkwrap.json` (npm), `pnpm-lock.yaml`
     (pnpm) or `yarn.lock` (yarn) up the tree, for the listed ecosystems (workspaces keep one lockfile at the root);
   - a changed lockfile of a listed ecosystem → that lockfile;
   - nuget: any changed `*.csproj` / `*.fsproj` / `*.vbproj`, `Directory.Packages.props`, `Directory.Build.props` /
     `.targets`, `packages.lock.json` or `nuget.config` → `dotnet restore -p:RestoreLockedMode=false` of `projects`
     and of every project beside a tracked `packages.lock.json`. No `--force-evaluate`, which re-floats versions.
4. **Rebuilds the three-way markers** (O35 #2) for an npm, pnpm or Yarn lockfile when HEAD's copy is the base
   branch's, the pull request changed it, and the merge base has it: `git merge-file -p --diff3` of (base branch,
   merge base, pull request), written over the lockfile. The package manager's lock-only install then resolves the
   markers and keeps both sides' pins. **diff3 is required for npm:** its `parse-conflict-json` rebuilds the common
   ancestor from the `|||||||` section, and without one the ancestor is not JSON and the install fails. Yarn 1
   skips that section; pnpm accepts it (both measured below). **Yarn 2+ ignores the markers' content:** finding
   `<<<<<<<` in `yarn.lock`, it runs `git rev-parse MERGE_HEAD HEAD`, reads `yarn.lock` from both commits and
   installs over their union (YN0048). The script therefore writes the pull request head to `.git/MERGE_HEAD` for
   the duration of that one install and removes it afterwards; without it Yarn fails with YN0083 (measured in Linux
   containers, spark-regenerate README).
5. **Runs the lock-only install**, in the lockfile's directory:
   - npm: `npm install --package-lock-only --ignore-scripts --no-audit --no-fund`
   - pnpm: `pnpm install --lockfile-only --ignore-scripts --ignore-pnpmfile --no-frozen-lockfile`
   - Yarn 1: `yarn install --ignore-scripts --non-interactive --no-progress`; Yarn 2+:
     `YARN_ENABLE_SCRIPTS=0 yarn install --mode=update-lockfile --no-immutable`

   `--no-frozen-lockfile` and `--no-immutable` matter on Actions: both tools freeze the lockfile when `CI` is set.
   pnpm and Yarn run through `corepack` when `package.json`'s `packageManager` names them (the Yarn 1 that GitHub's
   runners ship refuses a project that declares `yarn@4`), else from the runner's PATH, else through `corepack`.
6. **Fixed point:** every install runs a second time over the first one's output and must change nothing, like
   `spark-regenerate`'s synchronize check. Same for the NuGet restores.
7. **Provenance** (O35 #3, [`provenance.mjs`](provenance.mjs), plain Node, no dependencies) over every lockfile the
   run changed or created. Every resolved package must exist, with the same identity, in the base branch's
   (`HEAD^`) or the pull request head's copy of that lockfile:

   | Lockfile | Identity |
   |---|---|
   | `package-lock.json` | `packages[*]` (or v1 `dependencies`): name, version, `resolved`, `integrity`; root, links and local folders skipped |
   | `pnpm-lock.yaml` | `packages:` key (peer suffix dropped) + `resolution` (`integrity`, `tarball`, `repo`/`commit`); directories skipped |
   | `yarn.lock` | Yarn 1: name, `version`, `resolved`, `integrity`; Yarn 2+: `resolution`, `checksum`; workspaces skipped |
   | `packages.lock.json` | `dependencies[tfm][name]`: name, `resolved`, `contentHash`; project references skipped |

   A violation fails the run and lists only `name@version (source host)`.
8. **Writes the patch** (standalone: [`run.sh`](run.sh)), the same shape as `spark-regenerate`'s, and uploads it as
   `merge-queue-patch`, empty or not.

## Fallback: no pull request head

Without `pr_number`, or when the fetch fails, or the head is not the one the queue prepared from:

- **No marker rebuild.** The lock-only install runs on HEAD's lockfile (the base side), so the pull request's new
  dependencies are re-resolved from their ranges, not taken from its lockfile. Measured below: a pull request that
  pinned `kind-of@6.0.0` under `^6.0.0` gets `6.0.0` with the rebuild and `6.0.3` in the fallback.
- **Provenance by host.** A package the base's lockfile does not hold must come from a host the base's lockfile
  already uses (for pnpm without a `tarball`, the default registry). It passes with a warning listing those
  packages. The registry still decides what a version resolves to; the check only stops a new source.
- **NuGet fails closed.** `packages.lock.json` records no source, so in the fallback a package the base does not hold
  is refused. Pass `pr_number` when NuGet lockfiles are listed.

## The pull request's head, and the token (T5)

The dispatch carries `pr_number` only, and no new dispatch input can be added: an undeclared one gets a 422. So
the action fetches `refs/pull/<n>/head` itself. GitHub serves that ref for public repositories anonymously; a
private repository needs a token. The design:

- **The job's own token, not a secret.** The `token` input defaults to `${{ github.token }}` inside the action, so
  the workflow references neither `secrets.*` nor any token, and the fork precondition (`MergeQueueForkGuard`:
  `contents: read` only, no secrets, no environment, GitHub-hosted runners) holds unchanged. With
  `permissions: contents: read` that token can only read this repository, which the job has checked out anyway.
- **Before any pull-request code.** The fetch is the action's first step: after `actions/checkout`, which runs
  nothing of the pull request, and before `setup-dotnet`, any package manager or any build.
- **Never on disk, never on a command line.** The header goes to git through `GIT_CONFIG_COUNT` /
  `GIT_CONFIG_KEY_0=http.<server>/.extraheader` / `GIT_CONFIG_VALUE_0` in that step's environment only. Nothing is
  written to `.git/config` (`persist-credentials: false` stays true), and the step's environment ends with the
  step. GitHub masks the token in logs.
- **What it does not change:** the job token exists for the whole job whether this step uses it or not
  (`actions/checkout` fetched with it too), and on a GitHub-hosted runner the pull request's code runs as a user
  with `sudo`, which could read the runner process's memory. That is why its scope, `contents: read`, is the real
  bound, as it was before this action.
- `token: ''` turns it off: public repositories fetch anonymously; a private one then takes the fallback.

## Why the pull request's code may run here, and never in the git writer

A lock-only install reads the repository's `.npmrc`, `.yarnrc.yml` (Yarn 2+ runs its `yarnPath` and plugins),
`pnpm-workspace.yaml` and `packageManager`; a NuGet restore evaluates MSBuild, `Directory.Build.*` and
`nuget.config`. All of that is the pull request's code. This workflow runs it in the consumer repository, on a
GitHub-hosted runner, with `contents: read` and no secrets (T5), and hands back only a patch, which the queue
limits to `regeneratedPaths` (T4). The git writer holds the App's write token and never runs repository code
(T5, D59), so lockfile regeneration can never move into it. Provenance closes what is left: a regenerated lockfile
is not what the reviewers approved, but every package in it is one that a reviewed lockfile already resolved.

## Running it locally

Every input is an environment variable. In a clean checkout of the prepared commit with its parent:

```bash
PR_NUMBER=7 bash .github/actions/lockfile-regenerate/fetch-pr-head.sh          # prints pr-head= and merge-base=
EXPECTED_SHA="$(git rev-parse HEAD)" LOCKFILES=npm PR_HEAD=<pr-head> MERGE_BASE=<merge-base> \
  PATCH_PATH=/tmp/merge-queue.patch bash .github/actions/lockfile-regenerate/run.sh
node .github/actions/lockfile-regenerate/provenance.mjs --file package-lock.json --base base.json [--head head.json]
```

The measurements are in [`spark-regenerate/README.md`](../spark-regenerate/README.md), "Verification (2026-10-15)".
