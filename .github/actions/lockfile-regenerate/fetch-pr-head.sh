#!/usr/bin/env bash
# Fetches the pull request's head and its merge base with the base branch, for the lockfile marker rebuild and the
# provenance check (README.md beside this file, "The pull request's head"). It must run BEFORE any of the pull
# request's code: it is the only step that may hold a token.
#
#   PR_NUMBER    required. The pull request (the dispatch's pr_number input).
#   FETCH_TOKEN  optional. The job's own token (contents: read), for a private repository. It is handed to git as an
#                http.extraheader through GIT_CONFIG_* environment variables of this process only: never written to
#                .git/config, never on a command line, gone when this step ends. Empty: fetch anonymously.
#   SERVER_URL   default $GITHUB_SERVER_URL, else https://github.com. The header is scoped to this URL.
#   REMOTE       default origin.
#
# Prints, and writes to $GITHUB_OUTPUT, `pr-head=<sha>` and `merge-base=<sha>`. A fetch that fails, or a merge base
# not found, is a warning with both left empty: lockfiles.sh then takes the fallback. Exit 1 only on a bad input.
set -uo pipefail

warn() { echo "::warning::$*" >&2; }
out() {
  echo "$1=$2"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then echo "$1=$2" >> "$GITHUB_OUTPUT"; fi
}
give_up() { warn "$* The lockfile step takes the fallback (README.md)."; out pr-head ""; out merge-base ""; exit 0; }

pr_number="${PR_NUMBER:-}"
[[ "$pr_number" =~ ^[1-9][0-9]{0,9}$ ]] || { echo "::error::pr_number must be a pull request number, got '$pr_number'." >&2; exit 1; }
remote="${REMOTE:-origin}"
server="${SERVER_URL:-${GITHUB_SERVER_URL:-https://github.com}}"
head_sha="$(git rev-parse HEAD)" || give_up "No HEAD."
ref="refs/spark-regenerate/pr-head"
git update-ref -d "$ref" 2>/dev/null || true

export GIT_TERMINAL_PROMPT=0
if [ -n "${FETCH_TOKEN:-}" ]; then
  header="AUTHORIZATION: basic $(printf 'x-access-token:%s' "$FETCH_TOKEN" | base64 | tr -d '\n')"
  unset FETCH_TOKEN
  export GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0="http.${server%/}/.extraheader" GIT_CONFIG_VALUE_0="$header"
  unset header
fi

shallow() { [ "$(git rev-parse --is-shallow-repository)" = "true" ]; }
merge_base() { git merge-base "$head_sha^" "$ref" 2>/dev/null; }

# The checkout is shallow (fetch-depth: 2). Fetch the PR head and deepen both tips until they meet; a full
# history as the last resort. Fetching the checked-out commit by SHA deepens the mq/ side without naming its branch.
mb=""
for depth in 50 400 3000 unshallow; do
  if [ "$depth" = unshallow ]; then
    shallow || break
    args=(--unshallow)
  else
    args=("--depth=$depth")
  fi
  if ! git fetch --no-tags --no-recurse-submodules --quiet "${args[@]}" "$remote" \
      "$head_sha" "+refs/pull/$pr_number/head:$ref" > /dev/null 2>&1; then
    give_up "Could not fetch refs/pull/$pr_number/head (a private repository needs the token input; a deleted head repository has no ref)."
  fi
  if mb="$(merge_base)"; then break; fi
  mb=""
  shallow || break
done
[ -n "$mb" ] || give_up "refs/pull/$pr_number/head and the base branch have no merge base."

out pr-head "$(git rev-parse "$ref")"
out merge-base "$mb"
