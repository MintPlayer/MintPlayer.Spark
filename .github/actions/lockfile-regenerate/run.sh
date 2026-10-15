#!/usr/bin/env bash
# The body of the lockfile-regenerate action (README.md beside this file): the standalone form, for a regenerate
# workflow that does lockfiles only (O36: coverage.yml's `action:` per autoResolve rule). spark-regenerate runs
# lockfiles.sh itself, inside its own patch. Every input is an environment variable:
#
#   EXPECTED_SHA  required. The commit the merge queue prepared. HEAD must be exactly this commit.
#   LOCKFILES     required. Ecosystems (lockfiles.sh).
#   PROJECTS, PR_HEAD, MERGE_BASE  passed through to lockfiles.sh.
#   PATCH_PATH    where the patch is written. Default: $RUNNER_TEMP/merge-queue.patch.
#
# Exit codes: 0 patch written (possibly empty), 1 any failure. Nothing is committed or pushed (D4, T4).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fail() { echo "::error::$*" >&2; exit 1; }

: "${EXPECTED_SHA:?EXPECTED_SHA is required}"
: "${LOCKFILES:?LOCKFILES is required}"
PATCH_PATH="${PATCH_PATH:-${RUNNER_TEMP:-$(mktemp -d)}/merge-queue.patch}"

expected="$(printf '%s' "$EXPECTED_SHA" | tr '[:upper:]' '[:lower:]')"
[[ "$expected" =~ ^([0-9a-f]{40}|[0-9a-f]{64})$ ]] || fail "expected_sha must be a full commit SHA, got '$EXPECTED_SHA'."
head="$(git rev-parse HEAD)"
[ "$head" = "$expected" ] || fail "HEAD is $head but the merge queue prepared $expected. The mq/ branch moved after dispatch; this run's patch would not belong to the prepared commit."

[ -z "$(git status --porcelain --untracked-files=all)" ] || {
  git status --short --untracked-files=all >&2
  fail "The working tree is not clean before regeneration; the patch would include these changes."
}

started=$SECONDS
bash "$here/lockfiles.sh"

# The same patch shape as spark-regenerate (and the writer's PatchValidator): through a throwaway index so new files
# are included, no renames, a/ b/ prefixes, no colour, no external diff.
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
GIT_INDEX_FILE="$scratch/index" git read-tree HEAD
GIT_INDEX_FILE="$scratch/index" git add -A
mkdir -p "$(dirname "$PATCH_PATH")"
GIT_INDEX_FILE="$scratch/index" git diff --cached --binary --no-renames --src-prefix=a/ --dst-prefix=b/ \
  --no-color --no-ext-diff HEAD > "$PATCH_PATH"
files="$(GIT_INDEX_FILE="$scratch/index" git diff --cached --name-only --no-renames HEAD)"
count=0
if [ -n "$files" ]; then count="$(printf '%s\n' "$files" | wc -l | tr -d ' ')"; fi

echo "Patch: $PATCH_PATH ($(wc -c < "$PATCH_PATH" | tr -d ' ') bytes, $count file(s)) in $((SECONDS - started)) s"
if [ -n "$files" ]; then printf '%s\n' "$files" | sed 's/^/  /'; fi

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "patch-path=$PATCH_PATH"
    echo "changed=$([ "$count" -gt 0 ] && echo true || echo false)"
    echo "file-count=$count"
  } >> "$GITHUB_OUTPUT"
fi
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### lockfile-regenerate"
    echo "Commit \`$head\`: $count lockfile(s) regenerated in $((SECONDS - started)) s."
    # shellcheck disable=SC2016 # literal Markdown backticks
    if [ -n "$files" ]; then printf '%s\n' "$files" | sed 's/^\(.*\)$/- `\1`/'; fi
  } >> "$GITHUB_STEP_SUMMARY"
fi
