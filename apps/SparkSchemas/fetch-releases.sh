#!/bin/sh
# Downloads the assets of every schemas/v{n} GitHub release into <output>/v{n}/ (#264, G-Q19/Q21).
#
# The releases are the archive: this is what makes the site rebuildable from scratch, and why every
# revision stays online. A revision is never edited after it is published (the publish workflow
# only ever creates v{n+1}), so serving them as immutable is safe.
#
# Usage: fetch-releases.sh <owner/repo> <output directory>
# A GitHub token in the BuildKit secret "github_token" is optional; it only lifts the API's
# anonymous rate limit (60 requests an hour per IP, shared by everything on a CI runner).
set -eu

repo="${1:?usage: fetch-releases.sh <owner/repo> <output directory>}"
out="${2:?usage: fetch-releases.sh <owner/repo> <output directory>}"
mkdir -p "$out"

set -- -fsSL --retry 3 -H "Accept: application/vnd.github+json"
if [ -s /run/secrets/github_token ]; then
  set -- "$@" -H "Authorization: Bearer $(cat /run/secrets/github_token)"
fi

assets="$(mktemp)"
page=1
while :; do
  json="$(curl "$@" "https://api.github.com/repos/$repo/releases?per_page=100&page=$page")"
  [ "$(printf '%s' "$json" | jq 'length')" -eq 0 ] && break
  # revision <TAB> asset name <TAB> download URL, for published schemas/v{n} releases only.
  printf '%s' "$json" | jq -r '
    .[]
    | select(.draft | not)
    | select(.tag_name | test("^schemas/v[0-9]+$"))
    | (.tag_name | ltrimstr("schemas/")) as $revision
    | .assets[]
    | select(.name | test("^[A-Za-z]+[.]schema[.]json$"))
    | [$revision, .name, .browser_download_url] | @tsv' >> "$assets"
  page=$((page + 1))
done

count=0
while IFS="$(printf '\t')" read -r revision name url; do
  mkdir -p "$out/$revision"
  # No Authorization header here: asset downloads redirect to a storage host, and the assets of a
  # public repository need none.
  curl -fsSL --retry 3 -o "$out/$revision/$name" "$url"
  count=$((count + 1))
done < "$assets"

revisions="$(find "$out" -mindepth 1 -maxdepth 1 -type d | wc -l)"
if [ "$count" -eq 0 ]; then
  # Before the first revision is published (a pull request's image check), there is nothing to
  # serve yet. The image still builds; the deploy only ever runs after a release exists.
  echo "warning: no schemas/v* release found in $repo; the site will serve nothing." >&2
else
  echo "Fetched $count schema file(s) in $revisions revision(s)."
fi
