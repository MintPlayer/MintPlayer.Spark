# Handoff — `compile-ts-action` in `MintPlayer/github-actions`

**Status: DELIVERED.** `compile-ts-action` is live on `MintPlayer/github-actions@main` (`deb622b`,
[PR #8](https://github.com/MintPlayer/github-actions/pull/8), closing
[#7](https://github.com/MintPlayer/github-actions/issues/7)), and both workflows here call it.
`MintPlayer/github-actions` also dropped its duplicate `coverage-upload` in `67d4552`
([#10](https://github.com/MintPlayer/github-actions/issues/10)). · **Raised by:**
[`../coverage_action_home_plan.md`](../coverage_action_home_plan.md) M0

> Kept as the record of what was asked for and why, and because the spec below is still the
> reference for the action's input surface. **It is no longer a to-do list**; the file contents in
> §§1-5 describe what now exists rather than what to create.

## What changed on the way in — read this before trusting §§1-5

The implementation deviated from this spec in six places, all correct, and two of them would have
shipped broken. Both were verified against the repo rather than argued about:

- **The drift check failed open.** `git status --porcelain -- <path>` exits **0 with empty output**
  for a pathspec matching nothing, and a failed command substitution inside `[ … ]` does not abort
  under `bash -e`. So the one-liner in §1 reported "no drift" — and `verify` went **green** — with no
  checkout, a typo'd `output-dir`, a build writing elsewhere, or a gitignored bundle. It is now
  `drift.sh`, which asserts its preconditions and fails closed.
- **The `token` input was declared, documented, and wired to nothing.** `publish.sh` pushed on
  whatever `actions/checkout` had persisted, so a caller passing a PAT silently got `GITHUB_TOKEN`
  behaviour. It is now applied as a git `extraheader`.

Also: the immutable-tag guard peels with `^{commit}`, because an annotated tag's own object SHA never
equals `HEAD` — `v2` and `v3` in that repo are annotated, so the drafted comparison was a live
failure; inputs reach bash through `env:` rather than `${{ }}` pasted into `run:`; `push` mode
refuses a non-branch ref; and `node -p` takes the path through `argv` instead of spliced into a JS
string literal.

**One defect remains open upstream:**
[github-actions#9](https://github.com/MintPlayer/github-actions/issues/9) — `publish.sh` displaces
the caller's credential by clearing `--local` config, which is not where `actions/checkout@v5+`
stores it. That produced two `Authorization` headers and an HTTP 400 on the first publish from this
repo. Worked around here by passing `token: ''`
(`.github/workflows/coverage-action-publish.yml`); remove that line when the issue is fixed.

## Why this is a handoff and not a commit

The action's source now lives at [`apps/CodeCoverage/action`](../../apps/CodeCoverage/action/README.md)
in this repository, but the TypeScript → single `index.js` build system must **not** be duplicated
here — that is goal G2 of [`../coverage_action_home_PRD.md`](../coverage_action_home_PRD.md). So the
pipeline is extracted once, into a composite action in `github-actions`, and both repositories call
it in two lines.

A session rooted in this repository cannot write to another repository, so the files below have to be
applied from a session rooted in `github-actions`.

## Sequencing — historical

~~`.github/workflows/pull-request.yml` and `.github/workflows/dotnet-build-master.yml` in **this**
repository already reference `MintPlayer/github-actions/compile-ts-action@main`. That path does not
exist yet, so **the `coverage-action` job here fails until this handoff lands**~~ — resolved: the
action exists, and the publish job moved to its own
[`coverage-action-publish.yml`](../../.github/workflows/coverage-action-publish.yml) so a `paths:`
filter could stop it firing on unrelated master pushes.

That is deliberate and visible rather than hidden behind a temporary pin: the alternative is pointing
at a branch ref that later needs a second commit to correct, which is how the previous move ended up
with five repositories pinned to an archived repo. Land this first, or expect one red job.

Nothing else breaks in the meantime — coverage uploads here use `uses: ./apps/CodeCoverage/action`,
which needs no external repository at all.

## What to create

### 1. `compile-ts-action/action.yml`

```yaml
name: 'Compile TS action'
description: 'Install, test and bundle a TypeScript GitHub Action, then either verify the committed bundle is current or commit and tag it'

# One place for the TypeScript -> single index.js pipeline every node action in the
# org needs. Callers supply where and what, never how.
#
# Two modes, one code path:
#   verify  rebuild and fail if the committed bundle differs. For pull requests.
#   push    rebuild, commit the bundle, push, and move the tags consumers pin.
#
# They differ only in the last step, so a green `verify` on a pull request is real
# evidence that the later `push` produces the same bytes.

inputs:
  working-directory:
    description: 'Directory holding the action package (its own package.json and lockfile).'
    required: false
    default: '.'
  output-dir:
    description: 'Bundle directory, relative to working-directory. Checked in verify mode, committed in push mode.'
    required: false
    default: 'dist'
  node-version:
    description: 'Node version used to build. Should match the action.yml `runs.using` runtime.'
    required: false
    default: '20.x'
  install-command:
    description: 'Dependency install. `npm ci` enforces the lockfile; `npm install` does not.'
    required: false
    default: 'npm ci'
  test-command:
    description: 'Test command. Empty string skips testing -- say so explicitly rather than by omission.'
    required: false
    default: 'npm test'
  build-command:
    description: 'Build command. Must write the bundle into output-dir.'
    required: false
    default: 'npm run build'
  mode:
    description: 'verify | push'
    required: false
    default: 'verify'
  commit-message:
    description: 'Commit subject used in push mode.'
    required: false
    default: 'build: repack the action bundle'
  major-tag:
    description: >-
      Moving tag force-updated to the commit just pushed (e.g. coverage-upload-v1). This is what
      consumers pin. Empty means do not tag.
    required: false
    default: ''
  version-tag-from:
    description: >-
      Path to a package.json (repo-relative) whose `version` mints an immutable tag. Empty means do
      not cut one.
    required: false
    default: ''
  version-tag-prefix:
    description: 'Prefix for the immutable tag, e.g. coverage-upload-v -> coverage-upload-v1.2.0.'
    required: false
    default: 'v'
  token:
    description: 'Token used to push. The default GITHUB_TOKEN does not trigger workflows, so committing the bundle cannot loop.'
    required: false
    default: ${{ github.token }}

outputs:
  changed:
    description: 'true when the rebuild produced a bundle different from the committed one.'
    value: ${{ steps.drift.outputs.changed }}
  version-tag:
    description: 'The immutable tag created, or empty.'
    value: ${{ steps.publish.outputs.version-tag }}

runs:
  using: composite
  steps:
    - name: Setup Node ${{ inputs.node-version }}
      uses: actions/setup-node@v4
      with:
        node-version: ${{ inputs.node-version }}

    - name: Install dependencies
      shell: bash
      working-directory: ${{ inputs.working-directory }}
      run: ${{ inputs.install-command }}

    # Before the build, so a failing suite cannot produce a published bundle.
    - name: Test
      if: inputs.test-command != ''
      shell: bash
      working-directory: ${{ inputs.working-directory }}
      run: ${{ inputs.test-command }}

    - name: Build
      shell: bash
      working-directory: ${{ inputs.working-directory }}
      run: ${{ inputs.build-command }}

    # `git status --porcelain`, NOT `git diff`: diff compares tracked files only,
    # so on a first-ever build the bundle is untracked, the diff is empty, and the
    # publish silently commits nothing. That bug is live in the workflow this
    # action replaces.
    - name: Detect bundle drift
      id: drift
      shell: bash
      run: |
        target="${{ inputs.working-directory }}/${{ inputs.output-dir }}"
        if [ -n "$(git status --porcelain -- "$target")" ]; then
          echo "changed=true" >> "$GITHUB_OUTPUT"
        else
          echo "changed=false" >> "$GITHUB_OUTPUT"
        fi

    - name: Verify the committed bundle is current
      if: inputs.mode == 'verify' && steps.drift.outputs.changed == 'true'
      shell: bash
      run: |
        target="${{ inputs.working-directory }}/${{ inputs.output-dir }}"
        echo "::error::$target is stale. Run '${{ inputs.build-command }}' in ${{ inputs.working-directory }} and commit the result."
        git status --porcelain -- "$target"
        git diff --stat -- "$target" || true
        exit 1

    - name: Commit, push and tag
      id: publish
      if: inputs.mode == 'push'
      shell: bash
      run: |
        "${{ github.action_path }}/publish.sh" \
          "${{ inputs.working-directory }}" \
          "${{ inputs.output-dir }}" \
          "${{ inputs.commit-message }}" \
          "${{ inputs.major-tag }}" \
          "${{ inputs.version-tag-from }}" \
          "${{ inputs.version-tag-prefix }}"
```

### 2. `compile-ts-action/publish.sh` (mode `chmod +x`)

```bash
#!/usr/bin/env bash
# Commits the rebuilt bundle and moves the tags consumers pin.
#
# Pushes with the credentials actions/checkout persisted, so no PAT and no deploy
# key. The default GITHUB_TOKEN does not trigger workflows, which is what keeps
# committing a bundle from looping; swapping in a PAT removes that guarantee and
# would need an explicit [skip ci].
set -euo pipefail

working_directory="$1"
output_dir="$2"
commit_message="$3"
major_tag="$4"
version_tag_from="$5"
version_tag_prefix="$6"

target="${working_directory}/${output_dir}"

git config --local user.name  'github-actions[bot]'
git config --local user.email '41898282+github-actions[bot]@users.noreply.github.com'

# -- the bundle ---------------------------------------------------------------
# `git add` then check the INDEX: --porcelain on the worktree would still report
# the path as clean-but-untracked on a first build.
git add -- "$target"
if git diff --cached --quiet -- "$target"; then
  echo "Bundle unchanged; nothing to commit."
else
  git commit -m "${commit_message}"
  # Not a force push: this branch carries other people's work. A non-fast-forward
  # here means someone pushed while we built, and failing is the correct answer.
  git push origin "HEAD:${GITHUB_REF_NAME}"
  echo "Pushed a rebuilt ${target}."
fi

# -- the immutable tag --------------------------------------------------------
version_tag=''
if [ -n "${version_tag_from}" ]; then
  version="$(node -p "require('./${version_tag_from}').version")"
  version_tag="${version_tag_prefix}${version}"

  if existing="$(git rev-parse -q --verify "refs/tags/${version_tag}" 2>/dev/null)"; then
    if [ "${existing}" = "$(git rev-parse HEAD)" ]; then
      echo "${version_tag} already points here."
    else
      # An immutable tag that moves is worse than no tag: an upload pinned to it
      # stops being reproducible. Bump the version instead.
      echo "::error::${version_tag} already exists on ${existing}. Bump the version in ${version_tag_from} instead of moving a released tag."
      exit 1
    fi
  else
    git tag "${version_tag}"
    git push origin "refs/tags/${version_tag}"
    echo "Cut ${version_tag}."
  fi
fi
echo "version-tag=${version_tag}" >> "$GITHUB_OUTPUT"

# -- the moving major tag -----------------------------------------------------
# Force, and ONLY for this tag. The workflow being replaced pushed with
# `tags: true, force: true`, which force-pushes every tag in the repository as a
# side effect of publishing a bundle.
if [ -n "${major_tag}" ]; then
  git tag -f "${major_tag}"
  git push -f origin "refs/tags/${major_tag}"
  echo "Moved ${major_tag} to $(git rev-parse --short HEAD)."
fi
```

### 3. `.github/workflows/pull-request.yml` — the repo has **none**

Today `publish.yml` is the only workflow, so the jest suite has never run in CI and bundle staleness
is never checked before merge. Owner decision (2026-09-01): run `mode: verify` for **every** action
there, not just `coverage-upload`.

```yaml
name: pull-request

on:
  pull_request:
    branches: [ main ]

permissions:
  contents: read

jobs:
  verify:
    name: verify bundles
    runs-on: ubuntu-latest
    timeout-minutes: 15

    steps:
    - uses: actions/checkout@v4

    # One call: this repo builds every action from one root package.json, so
    # `npm run all` rebuilds all six and the drift check covers dist/ wholesale.
    - uses: ./compile-ts-action
      with:
        working-directory: .
        output-dir: dist
        node-version: 20.x
        install-command: npm ci
        test-command: npm test
        build-command: npm run all
        mode: verify
```

Note `uses: ./compile-ts-action` — the repo verifies with its own copy, so a change to the compile
action is tested by the pull request that makes it.

### 4. `.github/workflows/publish.yml` — convert to the shared action

Replace the whole `steps:` block (setup-node, `npm install`, `npm run all`, the `git diff` check, the
commit step, `ad-m/github-push-action`) with:

```yaml
    steps:
    - uses: actions/checkout@v4
      with:
        fetch-depth: 0
        fetch-tags: true

    - uses: ./compile-ts-action
      with:
        working-directory: .
        output-dir: dist
        node-version: 20.x
        install-command: npm ci
        test-command: npm test
        build-command: npm run all
        mode: push
        commit-message: 'Pack with dependencies to dist'
```

and give the job `permissions: contents: write`.

Four behaviour changes, all deliberate:

| Before | After | Why |
|---|---|---|
| `npm install` | `npm ci` | The lockfile was not enforced while publishing a committed bundle. |
| no test step | `npm test` | The jest suite has never run in CI. |
| `git diff --name-only dist` | `git status --porcelain` | The diff misses untracked files, so a first-ever build commits nothing, silently. |
| `tags: true, force: true` | explicit per-tag push | Force-pushed every tag in the repo as a side effect of publishing. |

Also delete the commented-out blocks (`tj-actions/changed-files`, the `set-output` variants) and the
commented-out "Update Major Tag" step — the latter is now implemented properly in `publish.sh`.

### 5. `README.md`

Done: `compile-ts-action` is documented there, and the `coverage-upload` section was removed with the
duplicate itself — after M6, not before.

## ~~Do NOT delete `coverage-upload` yet~~ — done, in the right order

M7 happened after M6, as required: all five consumers were repointed to
`coverage-upload-v1` first, and only then did `MintPlayer/github-actions` drop its copy in `67d4552`.

## Verified

1. ✅ The five surviving bundles are **byte-identical** after a rebuild from the pruned dependency
   tree — blob hashes compared (`delay 1a840733e4a5` before and after), and CI agreed independently
   by adding no repack commit. PRD exit criterion 4.
2. ✅ `verify` fails on a stale bundle. It is also what guards this repo's own action:
   `pull-request.yml`'s `coverage-action` job runs `mode: verify`.
3. ✅ The `coverage-action` job here is green.
4. ✅ `npm test` runs in that repository's CI — for the first time, via the `pull_request` workflow
   §3 added, which now covers every action there rather than only `coverage-upload`.

One thing the definition of done did not anticipate: `drift.sh` decides on `git status --porcelain`,
which can report a **stat-dirty** index as modified even when content is identical (observed on a
Windows checkout: `git diff` clean and `git hash-object` equal, yet five files flagged). Harmless on
`ubuntu-latest`, where freshly written files are re-hashed — but a `git update-index --refresh`
before the check would make it immune. Not filed; mentioned upstream on
[#10](https://github.com/MintPlayer/github-actions/issues/10).
