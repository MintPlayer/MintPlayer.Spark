# The upload API

The contract between a CI workflow and a Coverage server. Everything on this page is stable: fields
are added, never removed or repurposed.

Three endpoints, all under `/api/uploads`:

| | |
|---|---|
| `POST /api/uploads` | Send one coverage report bundle. Returns immediately; parsing is asynchronous. |
| `POST /api/uploads/finish` | Close the run's build now instead of waiting for the debounce. |
| `GET /api/uploads/status` | Ask how a run turned out. This is what a gate polls. |

Most workflows should use [the action](../action/README.md) rather than these endpoints directly —
it does the polling, the credential refresh and the back-off described below. This page exists so
that you can write the loop yourself, correctly, without reading any C#.

> **`/api/browse/*` is not part of this contract.** It is the web UI's internal API: undocumented,
> unversioned, anonymous, unmetered, and actively being reshaped as pages move onto the generic
> query surface. It is also structurally unable to answer the question a gate asks — it authorizes
> against *a signed-in human's* GitHub access, so an upload token cannot read a private repository
> through it, and it returns the same `404` for "no build yet" as for "not allowed", which is exactly
> the distinction a poller needs. Build on `/api/uploads/status`.
>
> **If you are polling `/api/browse` from CI today, move.** It is now rate limited **by client IP**,
> and GitHub-hosted runners share egress addresses — so your gate can be throttled by traffic that
> isn't yours, on a bucket you have no way to claim. `/api/uploads/status` is metered per *token*
> instead, so a CI caller gets a bucket of its own and is never collateral damage from someone
> else's crawler. That difference is the whole reason the endpoint exists.
>
> And whichever surface you poll: **never let a `429` count as a pass.** A gate that treats "I
> couldn't get an answer" as "the answer was fine" is worse than no gate, because it fails open
> exactly when the service is under load. Back off and retry — see the loop below.

---

## Authentication

Both credentials work on all three endpoints.

**Upload token** — `Authorization: Bearer covt_…`. Scoped to an account or a single repository.
Does not expire, so it is the simpler choice for a long poll.

**GitHub Actions OIDC** — `Authorization: Bearer <id-token>`, requires `permissions: id-token:
write`, and the token's **audience must be the server's base URL**. The `repository` claim *is* the
authorization: a workflow can only ever act on the repository it runs in. Unavailable to pull
requests from forks.

> **OIDC tokens expire after 5 minutes** (GitHub's maximum is 10, and it is not configurable). This
> is shorter than a build can take, so **a poll loop must re-mint**, not reuse the token it uploaded
> with. Call `core.getIDToken(<server base url>)` again when the current token is within a minute of
> its `exp`, and treat an unexpected `401` mid-poll as "refresh and retry once" before treating it as
> a failure. The action does this for you.

A request with no valid credential gets `401`. A request whose credential does not grant the named
repository gets **`404`, never `403`** — unknown and unauthorized are deliberately indistinguishable
so the API never confirms that a private repository exists.

---

## `POST /api/uploads`

`multipart/form-data`. Returns **`202 Accepted`** with `{"buildId": "…", "sessionId": "…"}`.

**202 means "accepted for processing", never "parsed".** Parsing happens on a background queue after
the response is sent. A 202 is not evidence that your reports were readable, that any file matched, or
that a number was produced — only `GET /api/uploads/status` can tell you that.

Key form fields: `repository` (`owner/name`), `commitSha` (**full** 40-character SHA — the PR *head*
sha, not the ephemeral merge commit), `runId`, `runAttempt`, `files` (one or more report files,
optionally gzipped — the format is sniffed, not declared), and `fileList` (the output of
`git ls-files`, which is what lets the server verify that report paths correspond to real files).
Maximum 50 MB per request.

Uploads sharing `(repository, commitSha, runId, runAttempt)` land on **one build** as separate
*sessions*, and are merged with max semantics — a line covered by any session is covered.

## `POST /api/uploads/finish`

`application/json`: `{"repository", "commitSha", "runId", "runAttempt"}`. Returns `202`.

Closes the build as soon as everything already uploaded has parsed, instead of waiting out the
2-minute debounce. **Call it from your last uploading job.** It is the difference between a gate that
finishes in seconds and one that waits two minutes for nothing, and it is the single biggest lever on
how long `wait-for-finalize` takes.

Finishing is queued behind the parses that preceded it, so it can never close a build on a
half-computed number.

---

## `GET /api/uploads/status`

```
GET /api/uploads/status?repository={owner}/{name}&commitSha={sha}&runId={id}&runAttempt={n}
Authorization: Bearer covt_…
```

```jsonc
{
  "buildId": "Commits/123/abc…/builds/456-1",
  "state": "Complete",              // ← the field to branch on
  "status": "Finalized",            // informational
  "finalizeReason": "Explicit",     // informational
  "createdAtUtc": "2026-08-18T09:12:03Z",
  "finalizedAtUtc": "2026-08-18T09:14:47Z",
  "coverage": {
    "linesCovered": 16184, "linesCoverable": 22051,
    "branchesCovered": 8795, "branchesTotal": 14867, "filesCount": 804
  },
  "baseline": {
    "sha": "e01681ec…", "branch": "master",
    "coverage": { "linesCovered": 16102, "linesCoverable": 21998, … }
  },
  "sessions": [
    { "sessionId": "…", "jobName": "test (ubuntu)", "flags": ["unit"],
      "parseStatus": "Parsed", "error": null, "filesCount": 804 }
  ],
  "commitUrl": "https://coverage.example.com/…",
  "feedbackState": "Posted"         // informational
}
```

### `state` — the contract

**`state` is the only field you should branch on.** It is derived on the server precisely so that
every consumer classifies a build identically, and so that new internal states can be introduced
without breaking anyone.

| `state` | Meaning | What a poller does |
|---|---|---|
| `InFlight` | The build is open, or some session has not finished parsing. | Keep polling. |
| `Complete` | Finalized, every session parsed cleanly. | **Terminal.** Use `coverage`. |
| `CompleteWithErrors` | Finalized, but at least one session failed (or the build timed out with work outstanding). | **Terminal.** `coverage` is real but **under-counts** — decide whether that fails your gate. |

These three values are **closed**. Anything new — for example the partial-parse state that will exist
once a session with *some* unreadable reports stops being reported as fully parsed — is absorbed into
`CompleteWithErrors`, because that already means "a real number that under-counts". You will not have
to handle a fourth value.

`coverage` is `null` while `InFlight`, and whenever no session produced any data at all.

### Termination — the four guarantees

1. **A build always finalizes, within 30 minutes of its creation.** Unconditionally, whether or not
   anything else happens. Typically it finalizes ~2 minutes after the last upload (the debounce), or
   within seconds of `POST /api/uploads/finish`.
2. **`InFlight` is not an absorbing state.** Once `status` is `Finalized`, every session is `Parsed`
   or `Failed`; the timeout converts any straggler. A session cannot sit in `Pending` forever, and a
   crashed parse reports as `Failed` with a message in `error`, not as silence.
3. **Terminal is not permanent.** A late upload for the same run **re-opens** the build, and `state`
   returns to `InFlight`. Merging keeps the result correct. A poller that saw a terminal state has a
   valid answer *for the uploads that had been made*; it must not assume the build is sealed forever.
4. **`finalizeReason == "Timeout"` implies something is wrong.** It is set only when the 30-minute
   ceiling arrived while a session was still parsing — those sessions are then marked `Failed`. A
   build that merely outlived 30 minutes with everything parsed closes as `Debounce`. So `Timeout`
   never appears without a failed session, and `state` will already be `CompleteWithErrors`.

**Therefore: `1800` seconds is the correct client timeout**, and it can never expire before the
server has made up its mind. A shorter one is a client policy decision ("I am not willing to wait"),
not a correctness requirement.

### The informational fields

`status`, `finalizeReason`, `parseStatus` and `error` are for **diagnostics and logs** — showing a
human why a gate failed. They are the server's internal vocabulary and it is **not frozen**: values
will be added as the parser gets more honest about partial failures.

Do not branch on them. That is what `state` is for.

| Field | Values today |
|---|---|
| `status` | `Open`, `Finalized` |
| `finalizeReason` | `Explicit`, `Debounce`, `Timeout` (null while open) |
| `sessions[].parseStatus` | `Pending`, `Parsed`, `Failed` |
| `feedbackState` | `Pending`, `Posted`, `Retry`, `Failed`, `Unavailable` (null before the first publish attempt) |

`feedbackState` says what happened to the check-run publish (`coverage/project` /
`coverage/patch`), so a workflow can tell "check-runs posted" from "this repo can't get
check-runs" without shell access to the server. Two caveats, both inherent:

- The publish is broadcast **after** finalize, so a poller can legitimately observe
  `state: Complete` while `feedbackState` is still `null` or `Pending`. Do not gate on it.
- `Posted`, `Failed` and `Unavailable` are terminal — the retry sweep only revisits `Retry`,
  and it gives up after 5 attempts. `Failed` therefore means *a new build is required* (e.g.
  after fixing the App's private key). `Unavailable` means the repository has no GitHub App
  installation (OIDC-only repos are a supported population) — deliberate silence, not an error.

### `baseline` — so a ratchet needs one call

The latest finalized coverage on the repository's **default branch**, excluding the commit being
polled, with the sha it came from. This is what a "coverage must not decrease" gate compares against,
and it is returned here so the gate never has to reach for a second, less appropriate API.

`baseline` is `null` when the default branch has no other finalized coverage yet — a first upload,
where a ratchet must pass by definition. Treat null as "pass", not as an error.

A null baseline is **not only a first-upload condition.** A default-branch workflow using
`cancel-in-progress: true` loses the superseded run before its upload step, so that commit simply has
no coverage — measured at ~5% of default-branch runs in a real consumer repo. Any gate that treats a
null baseline as an error will fail on perfectly healthy repositories; the correct behaviour is
always *abstain*.

*(If the repository was auto-provisioned by an OIDC upload rather than by installing the GitHub App,
the server does not know its default branch, and the baseline is taken from the polled commit's own
branch instead.)*

### Percentages

**None are stored or returned.** Coverage is counts — `linesCovered` / `linesCoverable` — and you
derive the rate. This is deliberate: a stored percentage drifts out of step with the counts it came
from, and rounding decisions belong to whoever is displaying the number.

Watch for `linesCoverable == 0`: naive division makes an uninstrumented file read as **100%**, which
in a gate turns "this has no tests at all" into a perfect score. Treat `0/0` as "no data", never as
success.

### Status codes

| Code | Meaning |
|---|---|
| `200` | The body above. |
| `401` | No valid credential. |
| `404` (empty body) | Unknown repository, **or** one your credential does not grant. Deliberately indistinguishable — do not retry, and do not infer existence. |
| `404` with `{"error": "No build for run …"}` | The repository is yours, but nothing was ever uploaded for that `runId`/`runAttempt`. Almost always a mismatched `runId` or an upload that never happened. Not a "keep waiting" state. |
| `429` | Rate limited. **Back off and keep waiting** — this is not a failure. Honour `Retry-After` when present. |

The two 404s are the distinction a gate needs: the first means *give up*, the second means *you asked
the wrong question*. Neither means *keep polling*.

---

## Writing the loop

```bash
deadline=$(( $(date +%s) + 1800 ))
interval=5

while :; do
  body=$(curl -sS -w '\n%{http_code}' \
    -H "Authorization: Bearer $COVERAGE_TOKEN" \
    "$COVERAGE_URL/api/uploads/status?repository=$REPO&commitSha=$SHA&runId=$RUN_ID&runAttempt=$RUN_ATTEMPT")
  code=$(tail -n1 <<<"$body"); json=$(sed '$d' <<<"$body")

  case "$code" in
    200) state=$(jq -r .state <<<"$json")
         [ "$state" != InFlight ] && break ;;
    429) interval=$(( interval * 2 > 60 ? 60 : interval * 2 )) ;;   # back off, keep waiting
    404) echo "no build for this run (or no access)"; exit 1 ;;
    *)   echo "unexpected $code"; exit 1 ;;
  esac

  [ "$(date +%s)" -ge "$deadline" ] && { echo "timed out"; exit 1; }
  sleep "$interval"
done

jq -r '"\(.state): \(.coverage.linesCovered)/\(.coverage.linesCoverable)"' <<<"$json"
```

Four things that loop gets right, and that are easy to get wrong:

- It exits on **any** non-`InFlight` state, rather than waiting for `Complete` specifically — a build
  that finishes with a failed session would otherwise poll until the timeout.
- It treats `429` as back-off and `404` as fatal. Reversing those produces either a gate that gives
  up under load, or one that hangs for half an hour on a typo in `runId`.
- Its deadline is wall-clock, not an iteration count.
- It reads `coverage` only after the loop, since the field is null while in flight.

Poll politely: every 5 seconds at first is fine, and the endpoint has a rate limit sized for that. If
you wait in several jobs of one workflow at once, they share the limit — prefer waiting in one job.

## Gating on the result

The service does not decide whether your coverage is acceptable — it reports. Compare `coverage`
against `baseline` yourself:

```bash
rate() { jq -r 'if .linesCoverable > 0 then .linesCovered / .linesCoverable * 100 else "null" end'; }
```

…and fail the step when the drop exceeds your tolerance. Give yourself a small tolerance rather than
requiring strict non-decrease: line counts move by a line or two for reasons that have nothing to do
with test quality.

**The service now also publishes `coverage/project` and `coverage/patch` check runs** directly to
GitHub for repositories with the App installed (with Checks read-write accepted — see the README's
permission notes). You can require those names in branch protection and delete the workflow-side
gate. The rules they judge by live in the repository's **Coverage gate** settings panel, overridable
per field by a `coverage.yml` in the repo — read from the **base ref**, so a pull request cannot
rewrite the policy it is judged by:

```yaml
gate:
  projectMode: auto        # auto = ratchet against the base; fixed = compare to projectTarget
  projectTarget: 80        # percent, fixed mode only
  projectThreshold: 1      # allowed drop, percentage points
  projectBasis: scoped     # what a partial build's project check judges: scoped | projection
  patchTarget: 80          # percent of added lines; omit to keep the patch check informational
  patchThreshold: 5
  blocking: false          # false (default): checks post numbers but never fail
```

Two rules the checks live by: **a missing baseline or diff is neutral, never red** (abstaining is
routine — see above), and with `blocking: false` the same numbers post with a neutral conclusion, so
you can watch the verdicts before letting them block anything.

## Partial uploads (`nx affected`)

A monorepo PR that runs `nx affected --target=test` measures only the affected projects. Declare
that, and the comparison stays honest instead of reading as a 98% collapse:

- Form fields `partial` (`true`) and `baseSha` (the sha your affected-computation ran against — what
  you passed to `nx affected --base`). Action inputs: `partial`, `base-sha`.

For a partial build, `GET /api/uploads/status` changes meaning in one place and adds two objects:

- **`baseline` becomes scoped**: the base commit's coverage restricted to the paths this build
  measured — a like-for-like ratchet. `coverage` (only the measured files) compares against it
  directly.
- **`projection`** is the patched whole-workspace total: the base build's per-file tree with your
  measured files overwritten and PR-deleted files pruned (via the uploaded `fileList`), summed at
  read time. It *asserts* unmeasured files unchanged — which is exactly what `nx affected` earns —
  and carries its own verdict: `complete` plus `incompleteReasons`
  (`baseWalked | noFileList | unmatchedPaths | parseErrors`). An incomplete projection is best
  effort; the UI shows a danger badge for it, and a strict gate should abstain on it
  (`projection-complete` action output).
- **`baselineScope`** states the denominator: `requestedBaseSha` vs `resolvedBaseSha` and
  `baseResolution` (`exact | mergeBase | walked | none`). The server uses your declared base when it
  has usable data for it; otherwise it resolves the PR merge-base via GitHub's compare API; otherwise
  it walks to the newest covered default-branch commit — and always tells you which it did. `none`
  means abstain, never error.

Partial builds never become the repository's headline number or badge. Patch coverage (`patch` in
the response) is computed from your own build's line data plus the diff, so it works even when the
base has no coverage at all; added lines in projects the run didn't measure are **skipped, not
counted as misses**.

Hazards to know: `nx affected` says nothing about workspace-root config changes (Nx ships
`sharedGlobals` empty), so an instrumentation-config change should be treated as a re-baseline; and
the comparison base for a stacked PR (branch-of-a-branch) usually widens to the default branch —
disclosed via `baseResolution`, degraded, never corrupted.

## Flags

Flags label an upload (`flags: unit,linux` on the action). Sessions carrying a flag additionally
merge into that flag's own per-file documents, so a finalized build reports per-flag totals: the
`flags` map on `/status`, `flagTotals` + a `?flag=` tree filter on the browse API, and flag chips on
the commit page. Flag names are sanitized to `[a-z0-9._-]` (they become document-id segments); builds
uploaded before flags had storage report no per-flag data — attribution can't be reconstructed from
merged documents.

## Retention

A **merged** pull request's build data (builds, per-file line data, per-flag documents, tree
summaries) is deleted when the `pull_request closed` webhook arrives; the commit keeps its summary
number for display. Closed-but-unmerged PRs keep their data. Repositories without the App installed
get no webhook and therefore no cleanup — a known, accepted gap. Anything that compared against a
since-deleted base degrades to the walk-back described above, disclosed in `baseResolution`.
