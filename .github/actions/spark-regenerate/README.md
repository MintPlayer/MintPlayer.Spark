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

Outputs: `changed` (`true`/`false`), `file-count`, `patch-path`.

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
not.

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
