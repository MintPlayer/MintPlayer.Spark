#!/usr/bin/env bash
# The body of the spark-regenerate action (README.md beside this file). Kept out of action.yml so it can
# be run, and tested, outside GitHub Actions: every input is an environment variable.
#
#   EXPECTED_SHA   required. The commit the merge queue prepared. HEAD must be exactly this commit.
#   PROJECTS       required. App .csproj paths, one per line (blank lines and #-comments ignored).
#   CONFIGURATION  default Debug. Must match what the repository's verify step runs.
#   BUILD_ARGS     extra `dotnet build` arguments, split on whitespace.
#   SKIP_BUILD     "true" reuses binaries already on disk (local runs only).
#   PATCH_PATH     where the patch is written. Default: $RUNNER_TEMP/merge-queue.patch.
#   LOCKFILES      optional. Ecosystems for ../lockfile-regenerate/lockfiles.sh, run before the build. Empty: skipped.
#   PR_HEAD, MERGE_BASE  optional, for lockfiles.sh (../lockfile-regenerate/fetch-pr-head.sh writes them).
#
# Exit codes: 0 patch written (possibly empty), 1 any failure. Nothing is ever committed or pushed: the
# patch is the whole output, and the queue validates and applies it (D4, T4).
set -euo pipefail

fail() { echo "::error::$*" >&2; exit 1; }

: "${EXPECTED_SHA:?EXPECTED_SHA is required}"
: "${PROJECTS:?PROJECTS is required}"
CONFIGURATION="${CONFIGURATION:-Debug}"
BUILD_ARGS="${BUILD_ARGS:-}"
SKIP_BUILD="${SKIP_BUILD:-false}"
PATCH_PATH="${PATCH_PATH:-${RUNNER_TEMP:-$(mktemp -d)}/merge-queue.patch}"

# 1. The checkout is the commit the queue prepared (D14). A full SHA only: an abbreviation can become
#    ambiguous, and the queue always knows the full one.
expected="$(printf '%s' "$EXPECTED_SHA" | tr '[:upper:]' '[:lower:]')"
[[ "$expected" =~ ^([0-9a-f]{40}|[0-9a-f]{64})$ ]] || fail "expected_sha must be a full commit SHA, got '$EXPECTED_SHA'."
head="$(git rev-parse HEAD)"
[ "$head" = "$expected" ] || fail "HEAD is $head but the merge queue prepared $expected. The mq/ branch moved after dispatch; this run's patch would not belong to the prepared commit."

# 2. A clean tree, so the patch holds only what synchronize wrote.
[ -z "$(git status --porcelain --untracked-files=all)" ] || {
  git status --short --untracked-files=all >&2
  fail "The working tree is not clean before synchronize; the patch would include these changes."
}

mapfile -t projects < <(printf '%s\n' "$PROJECTS" | sed -e 's/#.*//' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' | grep -v '^$' || true)
[ "${#projects[@]}" -gt 0 ] || fail "PROJECTS lists no project."
for p in "${projects[@]}"; do
  [ -f "$p" ] || fail "Project '$p' does not exist at $head."
done

# The patch is taken through a throwaway index, so new files (a new Model/<Entity>.json) are included
# and the caller's real index is never touched.
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
snapshot() {
  local out="$1"
  GIT_INDEX_FILE="$scratch/index" git read-tree HEAD
  GIT_INDEX_FILE="$scratch/index" git add -A
  # The plain shape the writer's PatchValidator parses: no rename detection (a delete plus an add), the
  # default a/ b/ prefixes whatever the user's diff config says, no colour, no external diff driver.
  GIT_INDEX_FILE="$scratch/index" git diff --cached --binary --no-renames --src-prefix=a/ --dst-prefix=b/ \
    --no-color --no-ext-diff HEAD > "$out"
}

started=$SECONDS

# 2b. Lockfiles (opt-in): regenerated before the build, so the build restores against them. Their changes are part
#     of the working tree from here on, so both synchronize snapshots, and the patch, include them.
if [ -n "$(printf '%s' "${LOCKFILES:-}" | tr -d '[:space:]')" ]; then
  PROJECTS="$PROJECTS" bash "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/../lockfile-regenerate/lockfiles.sh" \
    || fail "Lockfile regeneration failed (see above)."
fi

# 3. Build.
if [ "$SKIP_BUILD" != "true" ]; then
  # shellcheck disable=SC2086 # BUILD_ARGS is a list of arguments on purpose
  for p in "${projects[@]}"; do
    echo "::group::dotnet build $p"
    dotnet build "$p" -c "$CONFIGURATION" $BUILD_ARGS || fail "dotnet build failed for $p."
    echo "::endgroup::"
  done
fi
built=$SECONDS

# 4. Synchronize every project, model first (it seeds translations.json), then security. The same
#    `dotnet run --no-build -c <config>` form as the repository's verify step, so the regenerated files
#    are the ones verify accepts.
synchronize() {
  local p flag
  for p in "${projects[@]}"; do
    for flag in --spark-synchronize-model --spark-synchronize-security; do
      echo "::group::$flag $p (pass $1)"
      dotnet run --project "$p" --no-build -c "$CONFIGURATION" -- "$flag" || fail "$flag exited $? for $p (pass $1)."
      echo "::endgroup::"
    done
  done
}

synchronize 1
snapshot "$scratch/pass1.patch"
pass1=$SECONDS

# 5. The fixed point: a second synchronize over its own output must change nothing (§3.6). A generator
#    that does not converge would make every queue run produce a new diff.
synchronize 2
snapshot "$scratch/pass2.patch"
pass2=$SECONDS
if ! cmp -s "$scratch/pass1.patch" "$scratch/pass2.patch"; then
  diff "$scratch/pass1.patch" "$scratch/pass2.patch" | head -n 200 >&2 || true
  fail "Synchronize is not at a fixed point: the second run changed the output of the first."
fi

# 6. The patch. An empty file means "nothing to regenerate"; the queue tells that apart from a failed run
#    by the run's conclusion and the artifact's presence.
mkdir -p "$(dirname "$PATCH_PATH")"
cp "$scratch/pass1.patch" "$PATCH_PATH"
files="$(GIT_INDEX_FILE="$scratch/index" git diff --cached --name-only --no-renames HEAD)"
count=0
if [ -n "$files" ]; then count="$(printf '%s\n' "$files" | wc -l | tr -d ' ')"; fi

echo "Patch: $PATCH_PATH ($(wc -c < "$PATCH_PATH" | tr -d ' ') bytes, $count file(s))"
if [ -n "$files" ]; then printf '%s\n' "$files" | sed 's/^/  /'; fi
echo "Timing: build $((built - started)) s, synchronize pass 1 $((pass1 - built)) s, pass 2 $((pass2 - pass1)) s, total $((pass2 - started)) s"

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "patch-path=$PATCH_PATH"
    echo "changed=$([ "$count" -gt 0 ] && echo true || echo false)"
    echo "file-count=$count"
  } >> "$GITHUB_OUTPUT"
fi
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### spark-regenerate"
    echo "Commit \`$head\`: $count file(s) regenerated. Build $((built - started)) s, synchronize $((pass1 - built)) s + fixed-point check $((pass2 - pass1)) s."
    if [ -n "$files" ]; then printf '%s\n' "$files" | sed 's/^\(.*\)$/- `\1`/'; fi
  } >> "$GITHUB_STEP_SUMMARY"
fi
