#!/usr/bin/env bash
# Lockfile regeneration (README.md beside this file). Called by run.sh (this action) and by spark-regenerate's
# regenerate.sh, after their clean-tree check; it runs on its own too, as every input is an environment variable.
#
#   LOCKFILES   required. Ecosystems, one per line (spaces and commas also separate): npm, pnpm, yarn, nuget.
#   PROJECTS    optional. .csproj paths that nuget restores besides every project beside a packages.lock.json.
#   PR_HEAD     optional. The pull request's head commit, present locally (fetch-pr-head.sh). Empty: fallback.
#   MERGE_BASE  optional, with PR_HEAD. The merge base of PR_HEAD and HEAD^.
#   NODE        default node. Runs provenance.mjs.
#
# HEAD is the commit the queue prepared: the pull request squashed onto the base branch, HEAD^. What to regenerate
# comes from that squash (`git diff HEAD^ HEAD`) and, with PR_HEAD, from the pull request's own lockfile changes.
# Exit 0 done (possibly nothing to do), 1 any failure: a package manager error, no fixed point, a provenance violation.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fail() { echo "::error::$*" >&2; exit 1; }
warn() { echo "::warning::$*" >&2; }
note() { echo "lockfiles: $*"; }
node_bin="${NODE:-node}"

: "${LOCKFILES:?LOCKFILES is required}"
declare -A enabled=()
for eco in $(printf '%s\n' "$LOCKFILES" | sed 's/#.*//' | tr ',' ' '); do
  eco="$(printf '%s' "$eco" | tr '[:upper:]' '[:lower:]')"
  case "$eco" in
    npm|pnpm|yarn|nuget) enabled[$eco]=1 ;;
    *) fail "lockfiles: unknown ecosystem '$eco'; known are npm, pnpm, yarn and nuget." ;;
  esac
done
[ "${#enabled[@]}" -gt 0 ] || { note "no ecosystem listed, nothing to do."; exit 0; }

git rev-parse -q --verify 'HEAD^{commit}' > /dev/null || fail "No HEAD."
base="$(git rev-parse -q --verify 'HEAD^1^{commit}')" \
  || fail "HEAD has no parent in this checkout. The lockfile step reads the prepared squash's own diff (HEAD^..HEAD): check out with fetch-depth: 2."

exists_at() { git cat-file -e "$1:$2" 2> /dev/null; }
blob() { if exists_at "$1" "$2"; then git rev-parse "$1:$2"; fi; }
git_() { git -c core.quotepath=off "$@"; }

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

# ── The pull request's side ──────────────────────────────────────────────────────────────────────────────────
# PR_HEAD is used only if it is provably the head the queue prepared from: merging it onto HEAD^ again
# (git merge-tree, as the queue does) must give HEAD's content on every path that merged without a conflict. The
# conflicted paths are exactly where the queue chose a side or kept both, so they may differ. A head pushed after
# the queue prepared, or any other commit, fails this and is not trusted.
pr=""
mb=""
if [ -n "${PR_HEAD:-}" ]; then
  if ! pr="$(git rev-parse -q --verify "${PR_HEAD}^{commit}")" || ! mb="$(git rev-parse -q --verify "${MERGE_BASE:-}^{commit}")"; then
    warn "PR_HEAD or MERGE_BASE is not a commit in this repository; taking the fallback."
    pr="" mb=""
  else
    set +e
    git merge-tree --write-tree --name-only --no-messages -z "$base" "$pr" > "$scratch/merge-tree" 2> "$scratch/merge-tree.err"
    rc=$?
    set -e
    if [ "$rc" -gt 1 ]; then
      warn "git merge-tree failed ($(head -c 300 "$scratch/merge-tree.err")); taking the fallback."
      pr="" mb=""
    else
      tree="$(tr '\0' '\n' < "$scratch/merge-tree" | head -n 1)"
      tr '\0' '\n' < "$scratch/merge-tree" | tail -n +2 | grep -v '^$' | sort -u > "$scratch/conflicted" || true
      git_ diff --name-only --no-renames "$tree" HEAD | sort -u > "$scratch/differs"
      if [ -n "$(comm -23 "$scratch/differs" "$scratch/conflicted")" ]; then
        warn "refs/pull head $pr is not the head the queue prepared HEAD from (these paths merge cleanly yet differ: $(comm -23 "$scratch/differs" "$scratch/conflicted" | head -n 5 | tr '\n' ' ')); taking the fallback."
        pr="" mb=""
      fi
    fi
  fi
fi
if [ -n "$pr" ]; then
  note "pull request head $pr, merge base $mb (verified against HEAD)."
else
  note "no pull request head: no marker rebuild, and provenance falls back to the base lockfile's hosts (README.md)."
fi

changed="$(git_ diff --name-only --no-renames "$base" HEAD)"
pr_changed=""
if [ -n "$pr" ]; then pr_changed="$(git_ diff --name-only --no-renames "$mb" "$pr")"; fi

# ── npm, pnpm, Yarn ──────────────────────────────────────────────────────────────────────────────────────────
names_of() {
  case "$1" in
    npm) echo "package-lock.json npm-shrinkwrap.json" ;;
    pnpm) echo "pnpm-lock.yaml" ;;
    yarn) echo "yarn.lock" ;;
  esac
}

# The lockfile a package.json in $1 belongs to: the nearest one up the tree (workspaces keep one at the root).
nearest() {
  local dir="$1" name="$2" candidate
  while :; do
    if [ "$dir" = "." ]; then candidate="$name"; else candidate="$dir/$name"; fi
    if exists_at HEAD "$candidate"; then echo "$candidate"; return 0; fi
    [ "$dir" = "." ] && return 1
    dir="$(dirname "$dir")"
  done
}

declare -A js=()
while IFS= read -r path; do
  [ -n "$path" ] || continue
  case "/$path" in */node_modules/*) continue ;; esac
  file="$(basename "$path")"
  dir="$(dirname "$path")"
  for eco in npm pnpm yarn; do
    [ -n "${enabled[$eco]:-}" ] || continue
    for name in $(names_of "$eco"); do
      if [ "$file" = "$name" ] && exists_at HEAD "$path"; then js[$path]=$eco; fi
      if [ "$file" = package.json ] && lock="$(nearest "$dir" "$name")"; then js[$lock]=$eco; fi
    done
  done
done < <(printf '%s\n%s\n' "$changed" "$pr_changed" | sort -u)

# pnpm or yarn: corepack when package.json pins it in `packageManager` (a global Yarn 1, as GitHub's runners ship,
# refuses a project that declares Yarn 4), else the one on PATH, else corepack.
pm_cmd() {
  if command -v corepack > /dev/null && grep -qE "\"packageManager\"[[:space:]]*:[[:space:]]*\"$1@" package.json 2> /dev/null; then
    echo corepack "$1"
  elif command -v "$1" > /dev/null; then echo "$1"
  else echo corepack "$1"; fi
}

pm() { # ecosystem, directory, [merge head]: the lock-only install, in that directory
  local eco="$1" dir="$2" merge_head="${3:-}"
  (
    cd "$dir"
    case "$eco" in
      npm) npm install --package-lock-only --ignore-scripts --no-audit --no-fund ;;
      pnpm)
        read -ra p <<< "$(pm_cmd pnpm)"
        "${p[@]}" install --lockfile-only --ignore-scripts --ignore-pnpmfile --no-frozen-lockfile ;;
      yarn)
        read -ra y <<< "$(pm_cmd yarn)"
        if [ "$("${y[@]}" --version | cut -d. -f1)" -ge 2 ]; then
          # Yarn 2+ ignores the markers' content: it resolves a conflicted yarn.lock from `git rev-parse MERGE_HEAD
          # HEAD`, taking the union of both commits' lockfiles. Name the pull request head as MERGE_HEAD meanwhile.
          if [ -n "$merge_head" ]; then
            mh="$(git rev-parse --git-path MERGE_HEAD)"
            [ ! -e "$mh" ] || { echo "::error::$mh already exists." >&2; exit 1; }
            trap 'rm -f "$mh"' EXIT
            echo "$merge_head" > "$mh"
          fi
          YARN_ENABLE_SCRIPTS=0 "${y[@]}" install --mode=update-lockfile --no-immutable
        else
          "${y[@]}" install --ignore-scripts --non-interactive --no-progress
        fi ;;
    esac
  )
}

# O35 #2: when the queue took the base side of this lockfile, the pull request's pins were dropped. Rebuild the
# three-way conflict (base branch | merge base | pull request) with plain diff3 markers and let the package manager
# merge it: npm's parse-conflict-json needs the ||||||| merge-base section, Yarn 1 and pnpm accept it. Yarn 2+ only
# needs markers present; it merges the two commits' lockfiles itself, given MERGE_HEAD (pm).
rebuild_markers() {
  local lock="$1" ours parent theirs rc
  [ -n "$pr" ] || return 1
  ours="$(blob HEAD "$lock")"; parent="$(blob "$mb" "$lock")"; theirs="$(blob "$pr" "$lock")"
  [ -n "$parent" ] && [ -n "$theirs" ] || return 1          # added or deleted on a side: nothing to merge
  [ "$theirs" != "$parent" ] || return 1                    # the pull request did not change it
  [ "$ours" != "$theirs" ] || return 1
  [ "$(blob "$base" "$lock")" = "$ours" ] || return 1       # HEAD's is not the base side: the queue merged it
  git cat-file blob "$ours" > "$scratch/ours"
  git cat-file blob "$parent" > "$scratch/parent"
  git cat-file blob "$theirs" > "$scratch/theirs"
  set +e
  git merge-file -p --diff3 -L "base branch" -L "merge base" -L "pull request" \
    "$scratch/ours" "$scratch/parent" "$scratch/theirs" > "$lock"
  rc=$?
  set -e
  [ "$rc" -lt 128 ] || fail "git merge-file failed on $lock."
  note "$lock: rebuilt the three-way merge ($rc conflict region(s)) for the package manager to resolve."
  return 0
}

mapfile -t js_locks < <(printf '%s\n' "${!js[@]}" | grep -v '^$' | sort || true)
declare -A pass1=()
for lock in "${js_locks[@]}"; do
  eco="${js[$lock]}"
  echo "::group::$eco: $lock (pass 1)"
  merge_head=""
  if rebuild_markers "$lock"; then merge_head="$pr"; else note "$lock: lock-only install on HEAD's lockfile."; fi
  pm "$eco" "$(dirname "$lock")" "$merge_head" || fail "$eco could not regenerate $lock."
  echo "::endgroup::"
  grep -qE '^(<{7}|\|{7}|={7}|>{7})' "$lock" && fail "$lock still holds conflict markers after $eco ran."
  pass1[$lock]="$(git hash-object --no-filters "$lock")"
done
# The fixed point: a second plain install over the first one's output must change nothing.
for lock in "${js_locks[@]}"; do
  echo "::group::${js[$lock]}: $lock (pass 2, fixed point)"
  pm "${js[$lock]}" "$(dirname "$lock")" || fail "${js[$lock]} failed on the second pass over $lock."
  echo "::endgroup::"
  [ "$(git hash-object --no-filters "$lock")" = "${pass1[$lock]}" ] \
    || fail "$lock is not at a fixed point: a second ${js[$lock]} install changed the first one's output."
done

# ── NuGet ────────────────────────────────────────────────────────────────────────────────────────────────────
nuget_locks=()
if [ -n "${enabled[nuget]:-}" ]; then
  trigger="$(printf '%s\n%s\n' "$changed" "$pr_changed" \
    | grep -iE '(^|/)(Directory\.(Packages|Build)\.(props|targets)|[^/]+\.(cs|fs|vb)proj|packages\.lock\.json|nuget\.config)$' || true)"
  if [ -n "$trigger" ]; then
    declare -A restore=()
    while IFS= read -r p; do [ -n "$p" ] && restore[$p]=1; done < <(printf '%s\n' "${PROJECTS:-}" \
      | sed -e 's/#.*//' -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' | grep -v '^$' || true)
    while IFS= read -r lock; do
      for p in "$(dirname "$lock")"/*.*proj; do [ -f "$p" ] && restore[$p]=1; done
    done < <(git_ ls-files -- 'packages.lock.json' '*/packages.lock.json')
    mapfile -t restore_list < <(printf '%s\n' "${!restore[@]}" | grep -v '^$' | sort || true)
    nuget_restore() {
      local p
      for p in "${restore_list[@]}"; do
        echo "::group::dotnet restore $p (pass $1)"
        # No --force-evaluate: it re-floats every version. RestoreLockedMode off: the lockfile is what changes here.
        dotnet restore "$p" -p:RestoreLockedMode=false || fail "dotnet restore failed for $p (pass $1)."
        echo "::endgroup::"
      done
    }
    nuget_snapshot() {
      git_ ls-files -co --exclude-standard -- 'packages.lock.json' '*/packages.lock.json' | sort | while IFS= read -r f; do
        printf '%s %s\n' "$(git hash-object --no-filters "$f")" "$f"
      done
    }
    if [ "${#restore_list[@]}" -gt 0 ]; then
      nuget_restore 1
      nuget_snapshot > "$scratch/nuget1"
      nuget_restore 2
      nuget_snapshot > "$scratch/nuget2"
      cmp -s "$scratch/nuget1" "$scratch/nuget2" \
        || fail "packages.lock.json is not at a fixed point: a second dotnet restore changed the first one's output."
      mapfile -t nuget_locks < <(cut -d' ' -f2- "$scratch/nuget1")
    else
      note "nuget: a project file changed, but there is no project to restore."
    fi
  fi
fi

# ── Provenance (O35 #3) ──────────────────────────────────────────────────────────────────────────────────────
# Every lockfile this run changed or created; one it left as HEAD holds only what the queue prepared from the two
# sides' blobs.
checked=0
for lock in "${js_locks[@]}" "${nuget_locks[@]}"; do
  [ -n "$lock" ] || continue
  if exists_at HEAD "$lock" && [ "$(git hash-object --no-filters "$lock")" = "$(blob HEAD "$lock")" ]; then continue; fi
  : > "$scratch/base.lock"; : > "$scratch/head.lock"
  if exists_at "$base" "$lock"; then git cat-file blob "$base:$lock" > "$scratch/base.lock"; fi
  head_args=()
  if [ -n "$pr" ]; then
    if exists_at "$pr" "$lock"; then git cat-file blob "$pr:$lock" > "$scratch/head.lock"; fi
    head_args=(--head "$scratch/head.lock")
  fi
  "$node_bin" "$here/provenance.mjs" --file "$lock" --base "$scratch/base.lock" "${head_args[@]}" --label "$lock" \
    || fail "The provenance check refused $lock."
  checked=$((checked + 1))
done
note "${#js_locks[@]} npm-family lockfile(s), ${#nuget_locks[@]} packages.lock.json considered; $checked changed and passed provenance."
