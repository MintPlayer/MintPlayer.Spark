#!/usr/bin/env bash
# The pipeline mode's proof (D17) needs three commits the shallow checkout lacks: the base branch's side (OURS_SHA),
# the pull request's side (THEIRS_SHA) and their merge base. This fetches them. It must run BEFORE any of the
# repository's code: it is the only step that may hold a token.
#
#   OURS_SHA, THEIRS_SHA  required, full SHAs.
#   MERGE_BASE            optional. When empty, both sides are deepened until `git merge-base` finds one.
#   PR_NUMBER             optional. refs/pull/<n>/head is fetched too, for a head a fork holds.
#   FETCH_TOKEN           optional. The job's own token (contents: read), for a private repository. Handed to git as an
#                         http.extraheader through GIT_CONFIG_* variables of this process only, never written to disk.
#   SERVER_URL            default $GITHUB_SERVER_URL, else https://github.com. The header is scoped to this URL.
#
# Prints, and writes to $GITHUB_OUTPUT, `merge-base=<sha>`. Exits 1 when a commit cannot be fetched: the run cannot
# prove anything, and the queue evicts the pull request.
set -uo pipefail

fail() { echo "::error::$*" >&2; exit 1; }
out() {
  echo "$1=$2"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then echo "$1=$2" >> "$GITHUB_OUTPUT"; fi
}
sha() { [[ "$1" =~ ^[0-9a-f]{40}$ ]]; }

ours="${OURS_SHA:-}"
theirs="${THEIRS_SHA:-}"
mb="${MERGE_BASE:-}"
sha "$ours" || fail "ours_sha must be a full commit SHA, got '$ours'."
sha "$theirs" || fail "theirs_sha must be a full commit SHA, got '$theirs'."
[ -z "$mb" ] || sha "$mb" || fail "merge_base must be empty or a full commit SHA, got '$mb'."
pr="${PR_NUMBER:-}"
[ -z "$pr" ] || [[ "$pr" =~ ^[1-9][0-9]{0,9}$ ]] || fail "pr_number must be a pull request number, got '$pr'."
server="${SERVER_URL:-${GITHUB_SERVER_URL:-https://github.com}}"

export GIT_TERMINAL_PROMPT=0
if [ -n "${FETCH_TOKEN:-}" ]; then
  header="AUTHORIZATION: basic $(printf 'x-access-token:%s' "$FETCH_TOKEN" | base64 | tr -d '\n')"
  unset FETCH_TOKEN
  export GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0="http.${server%/}/.extraheader" GIT_CONFIG_VALUE_0="$header"
  unset header
fi

base_refs=("$ours")
[ -z "$mb" ] || base_refs+=("$mb")
[ -z "$pr" ] || base_refs+=("+refs/pull/$pr/head:refs/format/pr-head")
shallow() { [ "$(git rev-parse --is-shallow-repository)" = "true" ]; }
have() { git cat-file -e "$1^{commit}" 2>/dev/null; }

found=""
for depth in 50 400 3000 unshallow; do
  if [ "$depth" = unshallow ]; then
    shallow || break
    args=(--unshallow)
  else
    args=("--depth=$depth")
  fi
  # By SHA first. A fork's head may be reachable only through refs/pull/<n>/head, so retry without it by name.
  if ! git fetch --no-tags --no-recurse-submodules --quiet "${args[@]}" origin "${base_refs[@]}" "$theirs" > /dev/null 2>&1; then
    git fetch --no-tags --no-recurse-submodules --quiet "${args[@]}" origin "${base_refs[@]}" > /dev/null 2>&1 \
      || fail "Could not fetch the commits the proof needs (a private repository needs the token input)."
  fi
  if [ -n "$mb" ]; then
    have "$ours" && have "$theirs" && have "$mb" && { found="$mb"; break; }
  elif found="$(git merge-base "$ours" "$theirs" 2>/dev/null)"; then
    break
  fi
  found=""
  shallow || break
done
have "$theirs" || fail "The pull request's commit $theirs is not on the remote."
[ -n "$found" ] || fail "$ours and $theirs have no merge base."
out merge-base "$found"
