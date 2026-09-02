# Coverage — the upload result contract

PRD + plan for [issue #9](https://github.com/MintPlayer/CodeCoverage/issues/9) — a consumer
requirements statement from `MintPlayer/mintplayer-ng-bootstrap`, the service's first real consumer
(73.4% lines / 59.1% branches over 804 files, uploading on every push to `master`), which wants to
build a PR merge gate and cannot, because **a workflow cannot read its own coverage result**.

Covers the issue body and the consolidated
[handover brief](https://github.com/MintPlayer/CodeCoverage/issues/9#issuecomment-5324780551), which
replaced the two earlier comments this document originally cited (they were deleted; nothing in them
was lost). Every claim below was re-verified against `master` on 2026-08-18 — see §0 for what moved.

Companion to [PRD.md](PRD.md), [PLAN.md](PLAN.md) and [roadmap-2026-08.md](roadmap-2026-08.md).
This document does not reorder the roadmap: it fills a gap the roadmap never had a task for, records
a decision the roadmap was waiting on, resequences one T0.3 item, and disarms one live defect.

Where an existing MintPlayer.Spark feature does the job, it is used rather than reimplemented — and
where it doesn't, the gap goes upstream rather than being quietly worked around. Both happened, and
the second one closed: Spark ships a migration runner this repo had never referenced and now uses
(§5 step 4); its rate limiter placed its middleware on the wrong side of authentication for this app
and could not be pointed at `/api/browse`, so those gaps were filed as
[Spark#265](https://github.com/MintPlayer/MintPlayer.Spark/issues/265), fixed in
[#266](https://github.com/MintPlayer/MintPlayer.Spark/pull/266), and the local workaround is now
deleted in favour of the framework call (§6.1, N5).

Legend: 🟦 Coverage repo · 🟪 `action/` · 🟩 MintPlayer.Spark PR

---

## 0. What the 2026-08-18 re-verification changed

The first draft of this document was written against the issue's two original comments. Those were
replaced by a single consolidated handover brief, so every claim was read again in the source. **The
design survives; seven things moved, and five of them are new constraints rather than corrections.**
An eighth (0.8) was found later, while building, and is a finding about the app rather than about the
issue.

| # | Finding | Effect |
|---|---|---|
| 0.1 | **The baseline cannot come from `Repository.LatestCoverage`.** That field *is* already the newest finalized default-branch coverage (`BuildFinalizer.cs:33-46`), which looked like a free baseline. But on a push-to-`master` gate the finalize that makes `state` terminal is the same finalize that overwrites it, so by the time the poller reads it the "baseline" is *this commit*. A second, independent reason found on re-read: `BuildFinalizer.cs:38-46` has no chronology guard, so finalizing an *older* default-branch commit after a newer one overwrites the field with the older numbers — and while `DefaultBranch` is null the any-branch fallback re-applies on every finalize rather than seeding once. | §3.2 now specifies an explicit query over `Commits_ByRepository`, self-excluded. |
| 0.2 | **A status `GET` must not auto-provision.** `ResolveAuthorizedRepository` → `ResolveOidcRepository` (`UploadsController.cs:209-247`) **creates** `Account` + `Repository` documents for any public repo presenting an OIDC token. Reusing it verbatim on a `GET` would let a poll for a repository that never uploaded silently create it. | §3.2 uses a read-only resolve. |
| 0.3 | **The `uploads` policy is too tight to poll on.** 60 requests/minute per token (`Program.cs:170-178`). A 5-second poll is 12/min *per waiting job*; the consumer runs 13 report-producing jobs, and they share one token. Putting the status endpoint on that policy would 429 the gate **and** starve the uploads it shares a bucket with. | §3.2 adds a separate `uploads-status` policy on the same token partition; §3.3 makes the action honour `Retry-After`. |
| 0.4 | **`FinalizeReason == "Timeout"` is a stronger signal than §2 claimed.** `FinalizeBuildsCronJob:52` computes `reason = timedOut && !allParsed ? "Timeout" : "Debounce"`, and the `Timeout` branch marks every still-`Pending` session `Failed`. So `Timeout` ⟹ at least one `Failed` session, and a build that merely *exceeded* 30 minutes with everything parsed is labelled `Debounce`. | §2 guarantee 4 restated; the `Timeout` clause in the `state` derivation is belt-and-braces, not load-bearing. |
| 0.5 | **§6.1's "a `SizeLimit`, two lines, no API impact" is wrong.** `IMemoryCache` is registered once and shared (`GitHubAccessService.cs:21`, `GitHubContentService.cs:25`, plus anything Spark caches). Setting `SizeLimit` makes **every** `Set` that omits an explicit `Size` throw at runtime — including callers we don't own. | §6.1 replaces it with a dedicated bounded cache for source content. |
| 0.6 | **SP1 answered: GitHub OIDC ID tokens expire in 5 minutes** (10 maximum, not configurable). The action mints once, in `resolveCredential` (`main.ts:110`). A 30-minute wait on that one token 401s partway — on slow builds only. | §3.3 re-mints on expiry; SP1 closed. |
| 0.7 | **The consumer's cutover ask is already satisfied.** The handover brief §8 asks that the future check run be named `coverage/project` so their branch protection carries over unchanged. `roadmap-2026-08.md:399-400` already specifies `coverage/project` and `coverage/patch` — chosen independently, identical. | §4.4 records it as a **commitment**, so M11.3 cannot rename it. |
| 0.8 | **Every query in this document except the baseline runs on a RavenDB auto-index.** Found while adding an index for `Build` (N6). `Commits_ByRepository` is the only static index in the app; `/api/uploads/status`'s repository resolve (`UploadsController.cs:307`), the cron that makes `state` terminal (`FinalizeBuildsCronJob.cs:32`) and all nine browse endpoints are auto-indexed. Spark [#269](https://github.com/MintPlayer/MintPlayer.Spark/pull/269) (`preview.53`) can generate most of them from the entities — but **not `Commit`**: one index per entity is enforced as an error, and `AuthoredAt`'s coalesce and `HasCoverage`'s null test are not expressible. | New N6; §3.2's baseline query is unaffected and stays hand-written. Detail in `adopt-generated-indexes.md`. |

Everything else re-verified unchanged, including all of §5's `ParentSha` analysis (two writers, zero
programmatic readers, `Commit.json:134-148` editable), §6.2's authorization reading, and the three
copies of the visibility predicate. Two upstream facts were confirmed in the Spark source rather than
assumed: `MintPlayer.Spark.Migrations` **is published**, at the Spark version this repo pins (then
`preview.51`, now `preview.52`), and `Octokit.Webhooks` 4.1.2 does define `PullRequestBase` symmetrically with
`PullRequestHead`, so `pr.Base.Sha` is available to §5 step 2.

---

## 1. The issue's five items, after verification

The issue was filed against the deployed service and is largely accurate. Two items moved.

| # | Ask | Verdict |
|---|---|---|
| §1a | Document the terminal states | **Accepted.** Highest value per unit of effort, as the consumer says. The contract is *already true in code* — it has simply never been written down (§2). |
| §1b | Fault should report `Failed`, not `Pending` | **Already fixed** — stale. Shipped in `0a7619a` (#5), after the evidence in the issue was gathered (§1.1). |
| §1c | Action outputs + `wait-for-finalize` | **Accepted, with a design change**: the action polls a new narrow endpoint, not `/api/browse` (§3). |
| §2 | Vote for M11.1 patch coverage | **Noted; no resequencing.** Roadmap §10 already puts M11.1 at step 8. The vote confirms the order rather than changing it, and the consumer volunteers as a test bed (§4.3). |
| §3 | Data point on the §7.1 repo-config decision | **Decision recorded** — §7.1(1) resolved *yes*, as proposed (§4.2). |
| §4 | `ParentSha` carries two incompatible meanings | **Confirmed**, with the blast radius narrower than reported and the value's meaning worse — and the fix is one deleted line, not a migration (§5). |
| §5 | Is `/api/browse` a stable public contract? | **Answered: no.** It is the SPA's internal API. The contract is the new status endpoint instead (§3.1). |
| §A | Addendum: browse is anonymous *and* unmetered — confirming T0.3 from outside | **Confirmed, and worse than they could see** — the app never opts into Spark's own limiter either (§6.1). |
| §B | Addendum: `/spark/*` is a second anonymous read surface over the same data | **Confirmed exactly**, including that the authorization is sound. The duplication is narrower than they think, and cheaply fixable (§6.2). |
| §C | Addendum's real ask: when browse gets a limit, give machines a path that isn't collateral damage | **Already the design.** `/api/uploads/status` *is* that path — token-partitioned, on its own `uploads-status` policy (§3.2, §6.3). |

### 1.1 What already changed under the issue's feet

The consumer's §1(2) — *"any fault also reports as `Pending`; `ParseSessionRecipient.cs:95-102` saves
on the same faulted session, so the diagnosis is lost"* — was diagnosed independently in
[parse-session-stuck-pending.md](parse-session-stuck-pending.md) and **fixed**:

- `ParseSessionRecipient.ReportParseFailure` (`Coverage/Ingestion/ParseSessionRecipient.cs:139`) now
  opens a **fresh** session from the document store, precisely because the scoped one may be the thing
  that failed. The comment there names the exact symptom the consumer describes.
- `FinalizeBuildsCronJob` (`Coverage/Ingestion/FinalizeBuildsCronJob.cs:64-71`) now marks any session
  still `Pending` at the 30-minute timeout as `Failed ("Never parsed before the build timed out")`.

Those two together are what make §1a cheap: **`Pending` is no longer an absorbing state.** The
service already guarantees termination; it has never said so out loud.

The first-ever upload the consumer saw sit at `Pending` / `filesCount: 0` forever (run 31694883768)
was the request-budget exhaustion diagnosed in that document — same root cause, same fix.

---

## 2. The contract: termination, stated

The vocabulary exists in three places and is closed:

| Field | Values | Written by |
|---|---|---|
| `Build.Status` | `Open` \| `Finalized` | `UploadsController` (`Open`, and re-opens a finalized build on a late upload, `:85-90`); `BuildFinalizer.Finalize` |
| `Build.FinalizeReason` | `Explicit` \| `Debounce` \| `Timeout` | `FinalizeBuildRecipient` (explicit `/finish`), `FinalizeBuildsCronJob:73` |
| `BuildSession.ParseStatus` | `Pending` \| `Parsed` \| `Failed` | `UploadsController` (`Pending` on accept), `ParseSessionRecipient:112`, `:150`, `FinalizeBuildsCronJob:69` |

**The guarantees a poller may rely on:**

1. A build reaches `Finalized` **within 30 minutes of creation**, unconditionally
   (`TimeoutAfterCreation`, `FinalizeBuildsCronJob:24`), and typically ~2 minutes after the last
   upload (`DebounceAfterLastUpload`, `:23`) or immediately on `/finish`.
2. Once `Status == "Finalized"`, **every session is `Parsed` or `Failed`** — the timeout path
   converts stragglers. No session is `Pending` in a finalized build.
3. `Finalized` is **not permanently terminal**: a late upload re-opens the build
   (`UploadsController.cs:85-90`) and max-merge keeps the result correct. A poller that has observed
   `Finalized` has a valid answer for the uploads it made; it must not assume the build is sealed
   forever.
4. `FinalizeReason == "Timeout"` means *something did not arrive*, and it is narrower than it sounds.
   The cron labels a build `Timeout` only when the 30-minute ceiling hit **while a session was still
   `Pending`** (`FinalizeBuildsCronJob:52`); it then marks those sessions `Failed`. A build that
   merely outlived 30 minutes with everything parsed closes as `Debounce`. So **`Timeout` implies at
   least one `Failed` session** — it is the one reason a gate should treat as suspect, and it never
   appears alone.

**And the guarantee we deliberately do not give:** the `ParseStatus` vocabulary is **not frozen**.
T1.2 ("stop reporting partial parse failure as success") will add a partial state, because a session
where three of thirteen reports failed to parse is not `Parsed` — today it is
(`ParseSessionRecipient.cs:112`, `parsedAnything ? "Parsed" : "Failed"`). A consumer that switches on
the raw string will break when that lands.

So the contract is **one derived field**, not the internal vocabulary — see §3.2. Consumers branch on
that; the raw fields stay available for diagnostics, and are documented as informational.

---

## 3. Design: a narrow status endpoint, not `/api/browse`

### 3.1 Why not `/api/browse` — answering §5

`/api/browse` is **the SPA's internal API and is not a public contract.** The evidence, so the answer
is a finding rather than a preference:

- **No non-interactive credential can read a private repo through it.** Every endpoint gates through
  `ResolveVisibleRepository` (`BrowseController.cs:410-418`) → `IGitHubAccessService.IsOwnerAllowedAsync`,
  which resolves *the viewer's* access from their stored GitHub OAuth token. A `covt_` token does
  authenticate (the scheme is in the composite default, `Program.cs:85-86`) but its principal carries
  only `covt:*` claims and a synthetic `apitoken:` name (`ApiTokenAuthenticationHandler.cs:57-69`), so
  `userManager.GetUserAsync` returns null (`GitHubAccessService.cs:36-38`), allowed owners is `[]`,
  and **every private repo 404s**. OIDC is deliberately not a credential scheme at all
  (`Program.cs:83-84`). The consumer's public repo hid this entirely.
- **404 is overloaded.** An invisible repo, an unknown repo and a not-yet-uploaded commit are all 404
  — by design, for anti-enumeration. A gate cannot distinguish *"no build yet, keep waiting"* from
  *"you have no access, give up"*, which is precisely the distinction §1a asks for.
- **It is shaped for the SPA, and says so.** `GetCommit` (`:195`) and `GetFile` (`:350`) return
  anonymous objects assembled at the call site — no declared type to keep stable. `HistoryPoint`
  (`:86`) is documented *"ready for `bs-trend-chart`"*; `HierarchyNodeDto` (`:286-291`) exists to
  match `bs-hierarchy-chart`. `CoverageSummary`, `LineCoverage` and `BranchCoverage` are the **entity
  classes serialized directly** — there is no mapping layer to absorb a storage change.
- **It is already declared to be shrinking.**
  [adopt-spark-generic-ui.md:204-206](adopt-spark-generic-ui.md) — *"`/api/browse` shrinks but does
  not disappear. Tree/hierarchy/file/source endpoints …, `/api/me`, tokens, badges stay custom — they
  are not query-shaped."* It names what **stays**; everything query-shaped migrates to Spark's generic
  surface. `history`, `commits`, `branches` and `accounts/*` are exactly the query-shaped set — i.e.
  the consumer's chosen basis is the half with a standing plan to move, and `/file` (which they'd need
  for patch coverage) is the half that stays. Commits `22163e2` and `049fe9b` are that migration in
  progress.
- **It is unversioned, undocumented and unbounded.** No `[ApiExplorerSettings]`, no OpenAPI, no
  version segment, no contract test, and one in-repo consumer (`ClientApp/.../browse.service.ts`)
  whose hand-written interfaces duplicate the records. `take`, `skip` and `withCoverageOnly` have **no
  caller at all** and are unexercised.
- **A polling gate is the exact traffic shape T0.3 already warns about.** Unlike `/api/uploads`
  (`EnableRateLimiting("uploads")`) and `/badge` (`"badges"`), browse has **no rate limiter**
  (`Program.cs:167-189`), no cache headers and no ETag; and `GetFile` triggers a **live GitHub fetch
  per uncached path** into a process-wide `IMemoryCache` registered with no `SizeLimit`
  (`Program.cs:33`). The roadmap records this as *"an anonymous crawler over public repos exhausts
  memory and burns the installation's GitHub rate limit for every tenant"* (`roadmap-2026-08.md:152-155`,
  `[verified]`). **The client-side patch-coverage fallback the consumer describes in their correction
  comment — point-loading `/file` for every changed file — is that crawler.** Worth saying to them
  plainly, since it is currently their plan B.
- The endpoints the consumer expected and got 404s for — `/status`, `/pulls`, `/compare` — do not
  exist anywhere.

The consumer explicitly offered the right resolution: *"I'd rather know now and poll something
narrower."* So we build the narrow thing.

`/api/browse` gets a rate limiter and a one-line "internal, may change without notice" statement in
the README (N5). That is the honest answer, and it costs the consumer nothing they were promised.

**Four traps to hand them for the interim gate**, since they are building on `/history` and `/file`
this week and none of these is discoverable from the responses:

1. `/history`'s `take` is applied **before** the `LinesCoverable > 0` filter (`:107` then `:113`), so
   `take=100` can legitimately return fewer than 100 points. It caps at 500 and clamps a `0` to `1`.
2. `/history` is ascending (fetched descending, then `Reverse()` at `:111`) and `timestamp` is
   **nullable on the wire** for documents predating both stamps.
3. `/commits/{sha}` resolves by document id — **full SHA only**. A 7-character SHA is a 404, not a
   lookup.
4. `/file` requires the **normalized** path, exactly as `PathNormalizer` stored it, and a miss is a
   404 with no fuzzy fallback. The only reliable source of valid paths is `/tree` or `/hierarchy`. It
   also always answers for `commit.LatestBuildId` — no browse endpoint can address a specific build,
   so on a rerun it may answer about a different build than the one the workflow uploaded.

### 3.2 `GET /api/uploads/status`

Same controller and the same authorization *rules* as the upload itself
(`ResolveAuthorizedRepository`, `UploadsController.cs:170`), so it works for private repositories,
for upload tokens and for OIDC, and keeps the anti-enumeration 404.

**Two deviations from "just reuse the upload's resolve", both found in re-verification (§0.2, §0.3):**

- **It resolves read-only.** `ResolveAuthorizedRepository` delegates to `ResolveOidcRepository`
  (`UploadsController.cs:209-247`), which *auto-provisions* — it stores a new `Account` and
  `Repository` for any public repo presenting a valid OIDC token. That is right for an upload (the
  upload is the act of registering) and wrong for a `GET`: polling for a repository that never
  uploaded would create it as a side effect of reading. The status endpoint takes a `provision:
  false` path that loads and never stores; an unprovisioned repo is simply a 404.
- **It gets its own rate-limit policy, `uploads-status`.** The `uploads` policy allows 60
  requests/minute (`Program.cs:170-178`), sized for 50 MB uploads. A gate polling every 5 seconds
  spends 12/minute *per waiting job*, and the consumer's 13 jobs share one token — so sharing the
  bucket would 429 the gate and starve the uploads at the same time. `uploads-status` keeps the
  **same partition key** (`UploadsPartitionKey`, token hash falling back to IP — that is what §6.3's
  machine path means) with a limit sized for polling, not for payloads.

```
GET /api/uploads/status?repository={owner}/{name}&commitSha={sha}&runId={id}&runAttempt={n}
Authorization: Bearer covt_…   (or the OIDC workflow JWT)
```

```jsonc
{
  "buildId": "Commits/123/abc.../builds/456-1",
  "state": "Complete",          // ← THE contract. InFlight | Complete | CompleteWithErrors
  "status": "Finalized",        // informational: Open | Finalized
  "finalizeReason": "Explicit", // informational: Explicit | Debounce | Timeout
  "createdAtUtc": "…", "finalizedAtUtc": "…",
  "coverage": { "linesCovered": 16184, "linesCoverable": 22051,
                "branchesCovered": 8795, "branchesTotal": 14867, "filesCount": 804 },
  "baseline": { "sha": "e01681ec…", "branch": "master",
                "coverage": { "linesCovered": 16102, "linesCoverable": 21998, … } },
  "sessions": [ { "sessionId": "…", "jobName": "…", "flags": [],
                  "parseStatus": "Parsed", "error": null, "filesCount": 804 } ],
  "commitUrl": "https://coverage.mintplayer.com/…",
  "feedbackState": "Posted"     // informational: Pending | Posted | Retry | Failed | Unavailable (null before publish)
}
```

**`state` is the whole design.** It is derived server-side and is the only field a gate is invited to
branch on:

| `state` | Meaning | Poller |
|---|---|---|
| `InFlight` | build `Open`, or any session not yet terminal | keep polling |
| `Complete` | finalized, every session parsed cleanly | terminal — use `coverage` |
| `CompleteWithErrors` | finalized, but a session `Failed` **or** `finalizeReason == "Timeout"` | terminal — `coverage` is present but under-counts; the consumer decides whether that fails the gate |

This pulls the complexity downwards, which is the point: when T1.2 introduces a partial-parse state it
is absorbed into `CompleteWithErrors`, and no consumer changes. The consumer's own §1 ask — *"which
values are terminal, which retryable, which mean give up"* — is answered by a field instead of by
prose that every consumer must re-implement correctly.

`coverage` is `null` while `InFlight` and whenever no session produced any data.

**`baseline` is what makes the endpoint sufficient on its own.** A project ratchet — the gate the
consumer is actually building — needs two numbers: this build's, and something to compare against.
Without a baseline they must fetch the second from `/api/browse/…/history`, which puts them straight
back on the surface §3.1 just told them not to use, over a credential that cannot read private repos.
So the endpoint returns the latest finalized coverage on the repository's **default branch**,
excluding this commit, with the sha it came from.

Default branch specifically, because that is the comparison the consumer already chose — *"our gate
compares against the default branch precisely to avoid that field"* (their §4 note on `ParentSha`) —
and because it is the one baseline that needs no diff, no merge base and no `ParentSha`.

**How it is read, and the trap that rules out the cheap version (§0.1).** `Repository` already
carries `LatestCoverage` / `LatestCoverageSha` / `LatestCoverageAtUtc`, denormalized at finalize from
the newest default-branch build (`BuildFinalizer.cs:33-46`) — a free baseline on a document we have
already loaded. **It cannot be used.** On a push-to-`master` gate the very finalize that flips `state`
to terminal is the finalize that overwrites those fields with *this* commit, so the poller would
compare the build against itself and every ratchet would pass. The self-exclusion has to happen in
the query, not after it. So:

```
Query<Commits_ByRepository.Result, Commits_ByRepository>()
  .Where(r => r.Repository == repository.Id && r.Branch == baselineBranch && r.HasCoverage)
  .OrderByDescending(r => r.AuthoredAt)
  .OfType<Commit>().Take(2)          // 2, so the current commit can be skipped
```

The index already projects exactly these four fields (`Commits_ByRepository.cs:16-32`) and
`HasCoverage` is `Commit.Coverage != null`, which is assigned only at finalize — so a hit is by
construction a finalized build. One query.

`baselineBranch` is `repository.DefaultBranch`, falling back to **this commit's own branch** when it
is null: OIDC auto-provisioned repositories never learn their default branch (only the webhook path
sets it, `GitHubEventsRecipient.cs:130,142`), and that is precisely the population that uploads
without installing the App.

`baseline` is `null` for a repository whose default branch has no other finalized coverage yet — the
first-upload case, where a ratchet must pass by definition.

**`coverage` is the *build's*, not the commit's.** `Commit.Coverage` tracks whichever build finalized
last; on a rerun of an older run that is a different build than the caller uploaded to. The endpoint
answers about the build the caller named, which is the only honest answer to *"how did my run do?"*

**The 404 stays overloaded on the repository, and stops being overloaded on the build.** An unknown
or unauthorized repository returns the same bare 404 as `/api/uploads` — that is the anti-enumeration
property and it must not be weakened. But once the caller has proven authorization for the
repository, *"no build for this run/attempt"* is a distinguishable answer and gets one
(`{"error": "No build for run …"}`), because at that point it is a caller mistake — a mismatched
`runId`, or a poll for an upload that never happened — and not a secret. This is exactly the
distinction §1a asks for and browse structurally cannot give.

**Addendum 2026-09-02 — `assembly`.** The response gained a commit-level object beside the build's
own numbers: the union of every finalized build of the commit plus files carried unchanged (same git
blob OID) from the base, with `completeness`, `incompleteReasons`, measured/carried/unmeasured counts,
the base and the contributing `builds[]`. Nothing above changed: `state` keeps its three values,
`coverage` still means this build, and `assembly` is `null` until the first finalize and on servers
that predate it. Full description in `upload-api.md` § *`assembly`*; design in
`../coverage_carryforward_PRD.md`.

### 3.3 Action inputs and outputs

New inputs (all optional; the default stays fire-and-forget, as the consumer asked):

| Input | Default | Notes |
|---|---|---|
| `wait-for-finalize` | `false` | Poll until `state` is terminal. Implies nothing about `finish` — but pairing it with `finish: true` is the fast path (~seconds instead of the ~2-minute debounce). |
| `wait-timeout` | `1800` | Seconds. Matches the server's own 30-minute ceiling, so the default can never expire before the server has decided. |
| `wait-poll-interval` | `5` | Seconds; backs off to 15s after the first minute. |

New outputs, alongside today's `build-id` / `session-id` (`action/src/main.ts:69-70`):

`state`, `build-status`, `finalize-reason`, `lines-covered`, `lines-coverable`, `line-rate`,
`branches-covered`, `branches-total`, `branch-rate`, `files-count`, `commit-url`.

Rates are emitted as percentages rounded to one decimal — derived in the action from the pairs, never
stored (matching `CoverageSummary`'s own stated rule that percentages are derived so they can't
drift).

Also emitted: `baseline-line-rate`, `baseline-lines-covered`, `baseline-lines-coverable` and
`baseline-sha`, so the whole ratchet comparison is available to a workflow `if:` without a second
HTTP call. They are empty when the server returns no baseline (first upload).

**Failure semantics** follow the existing `fail-ci-if-error` input rather than inventing a second
knob: a wait that times out, or a `CompleteWithErrors`, emits `core.warning` and sets the outputs it
has; with `fail-ci-if-error: true` it fails the step. The action does **not** gate on a threshold —
comparing numbers is the consumer's job until M11.3 publishes a check run.

**Credential refresh — the SP1 answer (§0.6).** GitHub Actions OIDC ID tokens carry a **5-minute**
expiry (10 minutes is the documented maximum and it is not configurable), and the server validates
lifetime (`ValidateLifetime = true`, `Program.cs:146`). The action mints once today, in
`resolveCredential` (`main.ts:110`), which is fine for an upload measured in seconds and wrong for a
wait measured in minutes: the poll would 401 partway through — **only on slow builds**, the worst
shape of bug. The poller therefore decodes the token's `exp` and re-mints via `core.getIDToken(url)`
when it is within 60 seconds of expiry (~7 mints over a full 30-minute wait rather than one per
poll), and treats a 401 during polling as a refresh signal: re-mint once, retry, and only then give
up. Upload tokens (`covt_…`) never expire mid-wait and skip all of this.

**429 handling.** Even with the roomier `uploads-status` policy (§3.2), a matrix of waiting jobs
sharing one token can hit the limit. A 429 during polling is a *back-off*, not a failure: honour
`Retry-After` when present, otherwise double the interval up to 60s, and keep the overall
`wait-timeout` as the only thing that ends the wait. `postWithRetry`'s existing shape
(`main.ts:133-162`) already treats 429 as retryable — the poller reuses that judgement rather than
re-deciding it.

---

## 4. Decisions recorded

### 4.1 `/api/browse` is internal (§5)

Stated above and in the README. `/api/uploads/status` is the contract; it will keep its shape, and
new fields will only be added.

### 4.2 Roadmap §7.1(1) — repo config files: **yes**, as proposed

The consumer's data point resolves the pending call, and its reasoning is the reason:

- **Thresholds are not a property of a tree.** *"Our target is 80% lines / 72% branches, a standing
  decision that should not be rewritten by checking out an old commit."* → repo **setting**.
- **`ignore` absolutely is a property of a tree.** Their workspace generates `*.styles.ts`,
  `*.element.template.ts`, `*.generated.ts` and a `phone-core/src/metadata/**` subtree from source,
  and *which* patterns are generated changes commit to commit. → versioned **file**, per-field
  overriding writer over the settings document.
- **`Blocking` defaults to `false`.** *"A tool that starts blocking on install would simply get
  uninstalled."*
- **Policy is read from the base ref for every repository, not only public/fork ones.** *"A PR can
  lower its own gate is a problem regardless of who can open the PR."* Adopted — the narrower
  public-only rule in the roadmap is replaced by this.

This unblocks T1.5 / M11.2. No implementation here.

### 4.3 Sequencing is unchanged (§2)

Roadmap §10 stands: T0.1 backups, T0.2 token expiry, T1.1+T1.2, T1.4, T1.3+T1.6, T0.3+T0.4, then
M11.0, then M11.1. The consumer explicitly does not ask to jump the queue, and their correction
comment withdraws the "unblocking" claim: patch coverage *is* computable client-side today via
`/api/browse/…/commits/{sha}/file`, just not sensibly. M11.1 stays *highest-value*, not *blocking*.

The work in this document is small and orthogonal, and N1 in particular is prose. It does not
displace Tier 0.

### 4.4 The check-run names are a commitment, not a preference

The handover brief §8 explains that the consumer's interim workflow step is deliberately named
**`coverage/project`**, so that when M11.3 publishes a check run of that name they delete the step
and **branch protection carries over unchanged** — no coordination at cutover, and no window where
two things post the same status.

`roadmap-2026-08.md:399-400` already specifies `coverage/project` and `coverage/patch`, chosen
independently and identically. Recording it here promotes it from a design note to a **commitment**:
M11.3 publishes exactly those two names. Renaming them later is a breaking change for every consumer
that required the interim step, and the whole value of the coincidence is that nobody has to do
anything at cutover.

---

## 5. The `ParentSha` defect (§4)

**Confirmed. Two corrections to the issue's framing, one in each direction.**

- Writers: `Coverage/Recipients/GitHubEventsRecipient.cs:146` — `commit.ParentSha = evt.Before`,
  **unconditional**, in `OnPush`; and `Coverage/Controllers/UploadsController.cs:68` —
  `commit.ParentSha ??= form.ParentSha`, **conditional**. Webhook delivery and upload are unordered,
  so a push landing after an upload clobbers a PR base with a ref tip, exactly as reported.
- **Milder than reported:** the action only *sends* `parentSha` on PR events
  (`action/src/context.ts:36`), so on a push upload the `??=` is a no-op. The only path that ever
  writes a PR base is the PR-event upload — which is precisely the path `OnPush` can overwrite, so the
  collision is real but its blast radius is PR commits only.
- **Worse than reported:** `evt.Before` is not a parent even on its own terms. A push of five commits
  creates **one** Commit document — the head (`:144`) — so `ParentSha` becomes the tip five commits
  back. Branch creation stores the all-zero sha (`evt.Created` is never inspected, and no guard at
  `:136-137` catches it); a force-push stores the abandoned tip, which need not be an ancestor of
  `after` at all. Three of the six push shapes yield something that is not the head's parent, so the
  push writer's *own* stated meaning is unreliable independently of the collision.
- **Readers: none programmatic** — not server-side, not in the SPA, not in the action, not in tests;
  it is not even indexed (`Commits_ByRepository.cs:15-32`). `CoverageDelta` (`Commit.cs:46-64`)
  deliberately avoids it and says so. **But it is not invisible:** `Commit.json:134-148` declares it
  `showedOn: "PersistentObject"`, `isReadOnly: false`, so it renders as an editable "Parent Sha" field
  on the generic Spark commit page — a human can write a third meaning into it.
- **No test net.** There is no `GitHubEventsRecipient` test file at all; the push and PR handlers have
  zero coverage, and there is no fake for the webhook path (`GitHubAuthTestFakes` covers only
  OAuth/token services). A webhook-ordering regression test is net-new infrastructure, not an
  afterthought — cost it accordingly.

### The fix, sequenced so the cheap half carries the whole benefit

The trap is disarmed by **deleting one line**, and that step needs no model change, no migration and
no wire change:

1. **`OnPush` stops writing the field.** Deleting `commit.ParentSha = evt.Before;` (`:146`) leaves
   exactly one writer and one meaning. It does not touch the CLR shape, so no synchronize, no
   `modelHashes.json`, no deployment gate. `evt.Before` has no consumer and, per above, no correct
   use.
2. **`OnPullRequest` starts writing it** from `pr.Base.Sha` with a plain `=`. Also no shape change —
   it assigns the existing property. `pr.Base` is available (Octokit.Webhooks 4.1.2 defines
   `PullRequestBase` symmetrically with the `pr.Head` this method already dereferences twice), and the
   method already keys on `pr.Head.Sha` (`:158`), the *same* document the upload targets — webhook and
   upload converge by construction. Unconditional `=` is correct here, and for a better reason than
   "the webhook is authoritative": when the base branch advances, GitHub re-sends `synchronize` with
   an updated `pr.Base.Sha`, so `=` keeps it current where `??=` would freeze the first-seen base. A
   `synchronize` never corrupts an earlier commit, because a moved head is a new document key.
3. **Rename to `PullRequestBaseSha` — deferred, not skipped.** After (1) and (2) the field is correct
   and singly-owned; the rename buys honesty in the UI and nothing functional, and it is the one
   option that is genuinely expensive:
   - CLR shape change → `--spark-verify-model` fails CI (exit 3) and, more seriously, **a production
     app now refuses to start on a model mismatch** (`SparkModelOutOfSyncException`). Regenerated
     `Commit.json` **and** `modelHashes.json` must ship in the same commit.
   - The wire name `parentSha` (`UploadsController.cs:157`, `context.ts:8,36`) would have to be kept
     as an alias regardless: **the action is unversioned**, so consumers pinned to an older `@master`
     still post the old name.
   Do it when something else forces a synchronize anyway. Until then, flip the field to
   `isReadOnly: true` in `Commit.json` so the generic page cannot inject a third meaning.
4. **Stale values: drop them with a Spark migration.** Production has been live since 2026-08-13 and
   existing documents hold an unclassifiable mixture — PR bases, ref tips, zero shas, and nulls — with
   no discriminator, so no transform can recover a trustworthy base; the honest operation is to delete
   the property and let correct values repopulate on the next PR event.

   Coverage has no migration mechanism of its own (zero `PatchOperation` / `PatchByQueryOperation` /
   `DeleteByQueryOperation` anywhere), **but Spark ships one and we simply don't reference it**:
   `MintPlayer.Spark.Migrations` is absent from `Coverage.csproj:20-26`. `ISparkMigration` is exactly
   the right shape — discovered by source generator (no attribute, no base class), run once per
   database in `Version` order at startup after indexes and before serving, guarded by a per-version
   marker document and a cluster-wide compare-exchange lock, with `[Inject]` dependencies like a cron
   job, and fail-fast on throw so a failed migration retries next start rather than half-applying.
   **Re-verified 2026-08-18 in the Spark source and on nuget.org**: every element above holds
   (`libs/migrations/MintPlayer.Spark.Migrations/ISparkMigration.cs`), the interface is
   `static abstract long Version` + `static virtual string? Description` + `Task UpAsync(CancellationToken)`,
   the runner is hooked into `UseSpark()` after index creation
   (`SparkMigrationsExtensions.cs:38-44`), and the package **is published at `10.0.0-preview.51`** —
   the exact version this repo already pins, so adopting it is a package reference and nothing else.
   `Demo/HR/HR/Migrations/M_202606081200_BreadcrumbDemo.cs` is the reference shape (a `partial class`
   with `[Inject]`, version as a sortable timestamp).

   So: add the package, `spark.AddMigrations()`, and one class whose `UpAsync` patches the collection.
   That is strictly better than the alternative — a hand-run RQL patch in Raven Studio
   (`from Commits update { delete this.ParentSha; }`), which is what the precedent in
   [reauth-on-401.md](reauth-on-401.md) would otherwise suggest — because it is committed, reviewed,
   replays automatically on a restored backup, and cannot be forgotten on a fresh environment.
   RavenDB tolerates the orphan property harmlessly either way, so this is hygiene rather than a
   blocker; the reason to do it properly is that **it is the repo's first migration and sets the
   pattern** for the ones T1.5 and M11.1 will need.

M11.1 still stores its own `PatchCoverage.BaseSha`, resolved at compute time from compare's
`merge_base_commit`. Even after this fix `pr.Base.Sha` is the base tip *at event time*, not the merge
base (`roadmap-2026-08.md:372-374`) — so the field is a hint for finding the PR, never the number's
base. The roadmap's "give patch coverage its own explicit `BaseSha`" stands unchanged; steps 1–2 exist
to stop the *existing* field lying, not to serve M11.1.

**Adjacent, worth knowing:** `Branch` has the identical write shape — `=` from both webhooks
(`:145,159`), `??=` from the upload (`UploadsController.cs:66`). It is benign only because all three
writers agree on the meaning. The `=`/`??=` asymmetry is a house pattern in that file, not a slip, so
a reviewer should not "fix" it by symmetry.

---

## 6. The addendum: two anonymous read surfaces, and the machine path

Handover brief §5–§6, posted after the issue body. Both claims verified; one is worse than reported
and one is narrower.

### 6.1 Browse is unmetered — and so is everything else (§A)

Confirmed as stated. But the more useful finding is a level up: **Spark ships a rate limiter and this
app never turns it on.**

`spark.AddRateLimiter()` (`MintPlayer.Spark/Extensions/SparkBuilderRateLimiterExtensions.cs:31`)
registers a fixed-window global limiter — 150 requests / 10 s per client IP by default
(`SparkRateLimiterOptions`) — and self-registers its middleware through the builder registry.
Coverage's `AddSpark` block (`Program.cs:36-121`) never calls it. The in-repo comment at
`Program.cs:150-151` — *"Spark's built-in rate limiter only covers `/spark/*`"* — describes what it
**would** cover if enabled, and has been read since as though it were.

Two consequences, in severity order:

- `/spark/*` is unmetered, as the consumer says — the general query surface over the same data. **This
  is the whole of the exposure here**, which is a smaller claim than the first draft made.
- **`/connect` does not apply to Coverage — verified, and this de-ranks the item.** Spark's limiter
  covers `/connect` as well as `/spark`, for a good reason its own source states: scoping to `/spark`
  alone *"meant an app that opted into the limiter still shipped an unthrottled password endpoint, and
  lockout — which is per-account — does nothing against an attacker spreading attempts across many
  accounts"* (`SparkBuilderRateLimiterExtensions.cs:47-50`). But those endpoints live in
  `MintPlayer.Spark.IdentityProvider`, and **Coverage does not reference that package**
  (`Coverage.csproj:20-26`) — there is no `/connect/login`, no password endpoint, no consent page.
  Coverage authenticates via GitHub OAuth only. The first draft ranked this above the crawler pending
  verification; verified, it is not an exposure at all here, and the crawler is the real item.

**One line fixes both**, using an existing Spark feature rather than a third hand-rolled policy.

**It composes with what we already have** — checked, because it was not obvious.
`spark.AddRateLimiter()` sets `rl.GlobalLimiter`, not a policy
(`SparkBuilderRateLimiterExtensions.cs:40-71`), and returns `GetNoLimiter` for every path outside
`/spark` and `/connect`. Coverage's own `AddRateLimiter(…)` call adds the named `uploads` / `badges`
policies to the same options object. Both configurators run; the global limiter is a no-op on
`/api/*`, so the ingest policies keep their exact current behaviour and nothing is double-counted
*by that*. The double-count comes from the middleware, not the limiter — see the trap in N5(1).

It does **not** fix `/api/browse`: the path scoping is hardcoded to `/spark` and `/connect`
(`:52`), by an explicit audit decision (finding L-3) to keep the framework out of rate-limiting
policy. Per the layering rule (`PRD.md` §2, generic → upstream, one PR per repo), the right move is a
**Spark PR** adding a `PathPrefixes` option to `SparkRateLimiterOptions` — a generic gap, not a
Coverage one — after which browse is covered by the same mechanism. Until that lands, a local
`browse` policy alongside `uploads`/`badges` is the stopgap.

The consumer's other two suggestions stand — but the first one **cannot be implemented the way it is
written**, and the reason is worth stating because it looks like a two-line change (§0.5).

`AddMemoryCache()` (`Program.cs:33`) registers **one shared `IMemoryCache`** for the whole app;
`GitHubContentService` (`:25`) and `GitHubAccessService` (`:21`) both take it, and so may anything
inside Spark. `MemoryCacheOptions.SizeLimit` is not a ceiling that silently evicts — once it is set,
**every `Set` that omits an explicit `Size` throws** `InvalidOperationException`. Bounding the shared
cache therefore means auditing every caller in the process, including ones we do not own, and a
missed one is a runtime failure on a code path that used to work.

So: **give source content its own bounded cache** instead. A dedicated `MemoryCache` with a
`SizeLimit`, entries sized by content length, injected into `GitHubContentService` only — the
unbounded growth §5 describes is entirely that one cache of GitHub file bodies, and the shared cache
(short-lived owner lists) is not the problem. Same effect, no blast radius. The per-request cap on
`/file`'s GitHub fetches is unchanged and cheap.

### 6.2 The second read surface (§B)

Confirmed exactly. `MapSpark()` (`Program.cs:218`) mounts the generic query API, and
`App_Data/security.json` grants `QueryRead` on **Account, Repository, Commit and Build** to the
`Everyone` group — which includes anonymous callers. Their read of the authorization is also correct:
`RepositoryActions.GetRowFilterAsync:38` reduces to `!r.IsPrivate` for anonymous viewers,
`BadgeToken` is a protected attribute for non-managers, and no `Edit`/`New`/`Delete` right exists
anywhere in `security.json`, so the surface is read-only by construction. **No leak.**

**One refinement they could not see from outside: the duplication is narrower than they think.** Both
surfaces already share a single source of truth for *who you are* — `RepositoryActions` goes through
`ISparkVisibility`, which delegates to the same `IGitHubAccessService.GetAllowedOwnersAsync()` that
`BrowseController` uses (`SparkVisibility.cs:19-20`), memoized per request. What is duplicated is only
the *predicate shape* `!IsPrivate || OwnerLogin.In(owners)` — written three times
(`SparkVisibility.cs:37`, `RepositoryActions.cs:38`, and imperatively in
`BrowseController.ResolveVisibleRepository`).

So the fix is not "unify two authorization systems"; it is **extract one predicate**, plus their
suggested test asserting both surfaces return the same repo set for the same principal. Cheap, and it
makes the doc-comment's promise ("same semantics as `BrowseController.ResolveVisibleRepository`")
enforced rather than asserted. Folded into N5.

### 6.3 The ask hidden in the addendum (§C)

> *"When browse gets authn or a rate limit, give a token-authenticated read path an exemption or a
> higher bucket, and I'll move the gate onto it. Otherwise the fix for the abuse case silently breaks
> the legitimate one."*

This is the right instinct and it is **already what N2 is**. `/api/uploads/status` sits on its own
`uploads-status` policy sharing `UploadsPartitionKey`, which partitions by
`ApiTokenService.Hash(token)` rather than by IP (`Program.cs:156-165`) — so a CI caller gets its own bucket, is never collateral damage from a
crawler, and never shares a bucket with GitHub's camo proxy. The design answers the ask by
construction; the sequencing answer is that **N2 ships before or with N5**, so the machine path exists
before the anonymous one is bounded. That ordering is the whole of their concern, and it costs
nothing to honour.

---

## 7. Spikes

Each is a question whose answer changes the implementation, and each is cheap. **SP1 and SP3 were run
on 2026-08-18 and are closed; SP2 and SP4 stay open, and neither blocks anything in this document.**

### SP1 — Does an OIDC workflow JWT survive a long poll? 🟪 · ✅ **ANSWERED 2026-08-18 — no**

`AddJwtBearer(GitHubOidc.SchemeName)` sets `ValidateLifetime = true` (`Coverage/Program.cs:146`).

**Answer: a GitHub Actions ID token expires after 5 minutes** — 10 minutes is the documented maximum
and the value is not configurable ([actions/toolkit#2048](https://github.com/actions/toolkit/issues/2048),
[GitHub OIDC docs](https://docs.github.com/en/actions/concepts/security/openid-connect)). A
`wait-timeout` of 1800s outlives it six times over, so the single token minted in `resolveCredential`
(`main.ts:110`) **cannot** carry a wait.

**Remedy confirmed and adopted** (§3.3): decode `exp`, re-mint with `core.getIDToken(url)` within 60
seconds of expiry, and treat a polling 401 as a refresh signal before treating it as a failure. The
runtime request token (`ACTIONS_ID_TOKEN_REQUEST_TOKEN`) is valid for the job's lifetime, so
re-minting mid-job is supported; refreshing on expiry rather than per request keeps it to ~7 calls
across a full wait, which stays well clear of any minting limit. No fallback to upload-tokens-only is
needed, so `wait-for-finalize` works on both credentials.

### SP2 — What is the real time-to-finalize? 🟦 · cost S · **still open** (needs production traffic; blocks nothing)

Measure against production on `mintplayer-ng-bootstrap` (13 reports, 804 files) with and without
`finish: true`. Picks `wait-poll-interval` and the backoff curve, and tells us whether the ~2-minute
debounce is the dominant term — if it is, the docs should push `finish: true` harder than they do now.

### SP3 — Freeze the `state` mapping against T1.2 🟦 · ✅ **ANSWERED 2026-08-18 — three values, frozen**

T1.2 will introduce a partial-parse state, because `ParseSessionRecipient.cs:112` currently collapses
"some of my thirteen reports were unreadable" into `Parsed` whenever *any* file parsed.

**Answer: partial maps to `CompleteWithErrors`, and there is no fourth `state`.** The reasoning is the
one §3.2 is built on — `state` exists so that the classification lives on the server. A separate
`Partial` value would hand it straight back: every consumer would have to decide, independently and
probably differently, whether partial is a pass. `CompleteWithErrors` already means exactly *"this is
a real number, and it under-counts"*, which is what a partial parse is; the raw `sessions[]` remains
available for anyone who wants to know precisely which report failed and why.

The consequence for N1's prose is that it may state the three values as **closed**: T1.2 will change
`parseStatus`, an informational field, and will not change `state`. That is the whole point of
deriving it.

### SP4 — What does the `Commit` grid sort on once it is index-backed? 🟦 · cost S · **still open**

Opened by §0.8. `Repository_Commits` declares `Date` as its default sort, and `Date` is a get-only
`AuthoredAt ?? FirstSeenAtUtc` on the entity; `CoverageDelta` is another sortable grid column that is
`[JsonIgnore]` and exists in RavenDB not at all. Both work today only because
`CommitActions.Repository_Commits` materializes and returns an in-memory `IQueryable` — and
`CommitActions.cs:53-57` already says in as many words that the sort must move to an indexed field if
that ever stops being true.

So the question is not *whether* the Commit grid can be index-backed but what the grid sorts on when
it is: a mapped `Date` on the hand-written index (which `Commits_ByRepository` already computes, under
the name `AuthoredAt`), a changed default sort in the model, or leaving it materialized — in which
case its unbounded `ToListAsync()` is a scaling problem on its own terms.

Blocks only step 5 of [adopt-generated-indexes.md](adopt-generated-indexes.md); N6 as scoped here does
not touch it. Note that its step 6 — persisting `Date` and `HasCoverage` rather than computing them
in the index map — **answers this spike by removing it**: a materialized `Date` is sortable by
definition, and it is the same change that lets `Commit` be generated at all.

---

## 8. Milestones

### N1 — Write the contract down 🟦 · cost S (✅ BUILT 2026-08-18)

The consumer's own ranking: *"if only one of these is possible, (1) is the highest value per unit of
effort, and unblocks a consumer-side gate immediately."* It is prose over behaviour that already
holds, and it is the input to N2/N3 rather than an afterthought of them.

1. New `docs/upload-api.md`: `/api/uploads`, `/api/uploads/finish`, the state tables from §2, the four
   guarantees, and the explicit statement that the raw vocabulary is informational and open.
2. README: link it, and state that `/api/browse` is the SPA's internal API with no compatibility
   promise.
3. Document the `finish: true` fast path, since SP2 will likely show the debounce dominates.

**Exit criteria**: a reader can write a correct poll loop — including its timeout — from the docs
alone, without reading C#.

### N2 — `GET /api/uploads/status` 🟦 · cost S–M (✅ BUILT 2026-08-18)

Endpoint per §3.2, on `UploadsController` under a **new `uploads-status`** policy (§0.3 — not
`uploads`, whose 60/min is sized for 50 MB payloads and would 429 a poll), resolving the repository
**read-only** (§0.2 — the upload's resolve auto-provisions, which a `GET` must not do). Derive
`state` in one place — a static method on `Build`, so the SPA and any future check-run publisher
classify identically.

Tests: `InFlight` → `Complete`; a failed session → `CompleteWithErrors`; `Timeout` →
`CompleteWithErrors`; unknown repo and unauthorized repo both → 404 and are indistinguishable;
a known repo with no build for the run → 404 **with** a distinguishing body; the baseline excludes the
polled commit (the §0.1 self-comparison trap) and is null on a first upload; an OIDC poll for an
unknown public repo returns 404 **and stores nothing**.

**Exit criteria**: `curl` with a `covt_` token against a **private** repo returns a terminal `state`
for a finished build and `InFlight` for a live one — and a `baseline` sufficient to decide a ratchet
without a second call.

### N3 — `wait-for-finalize` and the outputs 🟪 · cost M (✅ BUILT 2026-08-18)

Per §3.3, with SP1 answered: the poller **must refresh an OIDC credential** (5-minute expiry) rather
than reuse the one minted for the upload, and must treat 429 as back-off rather than failure. Polling
lives in its own module with the retry/backoff shape `main.ts:133-162` already uses; `main.ts` stays a
thin sequence. Remember `npm run build` — `dist/index.js` is committed and is what `runs.main`
executes, so a source-only change ships nothing.

**Exit criteria**: a workflow step reads `steps.coverage.outputs.line-rate` and gates on it; with
`wait-for-finalize: false` the action's timing is unchanged.

### N4 — Disarm `ParentSha` 🟦 · cost S (steps 1–2) + M (step 4, first migration) (✅ BUILT 2026-08-18 — step 3, the rename, remains deferred by design)

Per §5, and **split so the benefit lands in the cheap half**: steps 1–2 (drop the push writer, add the
PR-webhook writer) are a few lines with no CLR-shape change, hence no model synchronize, no
`modelHashes.json`, no production start-up gate and no wire change. Step 3 (the rename) is explicitly
deferred to whenever something else forces a synchronize; flip `Commit.json` to `isReadOnly: true`
meanwhile so the generic PO page cannot write a third meaning.

Step 4 introduces `MintPlayer.Spark.Migrations` to the app — package reference plus
`spark.AddMigrations()` plus one `ISparkMigration` dropping the property. Cost it as M rather than S
because it is the **first** migration and sets the pattern T1.5 and M11.1 will follow, not because the
migration itself is hard.

Budget for the test net: there is **no `GitHubEventsRecipient` test file at all** and no fake for the
webhook path, so a webhook-ordering regression test is net-new infrastructure.

**Exit criteria**: no assignment of `evt.Before` survives; a PR upload followed by a push to the same
sha leaves the PR base intact (covered by a test, not by inspection); a fresh database and a restored
backup both come up with the stale property gone.

### N5 — Bound the anonymous surfaces 🟦 · cost S · **T0.3's item, resequenced to ship with N1** (✅ BUILT 2026-08-18 — items 1–3; item 4 filed upstream 🟩)

Four changes, in ascending cost — and the first is the one that matters most:

1. **Meter `/spark/*` and `/api/browse`** — the second read surface of §6.2, and our own.
   ✅ Now `spark.AddRateLimiter(o => o.PathPrefixes = ["/spark", "/connect", "/api/browse"])`,
   after [MintPlayer.Spark#266](https://github.com/MintPlayer/MintPlayer.Spark/pull/266) shipped in
   `preview.52`. The hand-rolled `GlobalLimiter` and the manual `app.UseRateLimiter()` are both
   deleted.

   **This document originally specified the framework call, then rejected it**, on two findings from
   reading the extension's body. Both were fixed upstream rather than worked around, which is why the
   rejection did not survive:
   - It bought no `/connect` protection here, because Coverage has no Identity endpoints (§6.1). Still
     true, and now irrelevant — `/connect` is simply one entry in a list we control.
   - **Its middleware landed after authentication**, and this app deliberately meters *before* it, so
     a flood costs no token lookup on the ingest path. `preview.52` introduces a middleware stage and
     registers the limiter at `BeforeAuthentication`, so the framework now places it exactly where the
     hand-rolled one sat.

   **The trap that made the two uncombinable is still live**, and is the reason the manual call had to
   go in the same commit as the switch: ASP.NET Core's `UseRateLimiter` has no idempotence marker and
   `RateLimitingMiddleware` records nothing to say it ran, so registering both puts two instances in
   the pipeline and every request takes two leases from the same partition — silently halving the
   configured budget.

2. **A dedicated bounded cache for GitHub source content.** Not a `SizeLimit` on the shared
   `IMemoryCache` as originally written — that would make every unsized `Set` in the process throw,
   including Spark's (§6.1). Sized in characters with a per-entry ceiling, so one generated bundle
   cannot evict everything else.
   The companion suggestion, *"a per-request cap on `/file`'s GitHub fetches"*, turns out to be a
   non-item: `GetFile` (`BrowseController.cs:350-380`) fetches exactly **one** path per request.
   There is no fan-out to cap. The real bounds are the cache ceiling and the request rate, which are
   (2) and (3).
3. **A local `browse` policy** alongside `uploads`/`badges`, as a stopgap until (4) — applied as an
   attribute on `BrowseController`, partitioned by IP, since these callers present no credential to
   key on. Deliberately a separate bucket from `uploads`: the machine path (§6.3) is the whole point.

   **The hazard this creates, and why §6.3's sequencing is not sufficient on its own.** Partitioning
   by IP is the only option for an anonymous surface, but GitHub-hosted runners share egress
   addresses — so a CI caller still polling `/api/browse` can be throttled by traffic that is not
   its own, which is exactly the collateral damage the consumer's Q3 asked us to avoid. Shipping the
   token-partitioned machine path in the same PR satisfies the letter of that request; it does not
   help anyone who has not yet moved onto it.

   Worse, it interacts with a decision on *their* side. Their D5 — *"treat a missing or `Pending`
   coverage result as skip, never as fail"* — is correct reasoning about outages, but composed with
   a 429 it makes the gate **fail open**: a throttled poll reads as a pass. Their own PRD sees the
   shape of this (*"a gate that 429s is worse than no gate, per D5"*) without knowing the limit had
   landed. So the bound is not done when the code ships: it is done when the consumer has been told
   to build stage 1 on `/api/uploads/status`, while their gate is still unimplemented and moving
   costs nothing. Documented in `upload-api.md` and raised on the issue.
4. **Upstream 🟩:** two gaps on `SparkRateLimiterOptions` — a `PathPrefixes` option (the `/spark` +
   `/connect` scoping is hardcoded, `SparkBuilderRateLimiterExtensions.cs:52`), and a way to place
   the middleware ahead of authentication (or at minimum a doc note that calling `UseRateLimiter`
   alongside it double-charges). Generic gaps, generic repo, per `PRD.md` §2 — after which (1) and
   (3) are deleted in favour of configuration. Filed as
   [MintPlayer.Spark#265](https://github.com/MintPlayer/MintPlayer.Spark/issues/265); context in
   [spark-handoff.md](spark-handoff.md).

Plus §6.2's cleanup: extract the `!IsPrivate || OwnerLogin.In(owners)` predicate written three times
(`SparkVisibility.cs:37`, `RepositoryActions.cs:38`, `BrowseController.ResolveVisibleRepository`) into
one place, and add the parity test asserting both surfaces return the same repo set for the same
principal.

**Sequencing constraint from §6.3**: N2 ships **before or with** this, so the token-authenticated path
exists before the anonymous one is bounded. Otherwise the fix for the abuse case breaks the legitimate
caller — which is precisely what the consumer asked us not to do.

**Exit criteria**: `/spark`, `/connect` and `/api/browse` are all metered; the memory cache has a
ceiling; a token-authenticated caller has its own bucket; the SPA is unaffected at normal use; the
visibility predicate exists once and a test pins the two surfaces together.

---

### N6 — Stop querying through auto-indexes 🟦 · cost S then M (✅ BUILT 2026-08-19 — wider than scoped)

Falls out of §0.8. Full investigation and sequencing in
[adopt-generated-indexes.md](adopt-generated-indexes.md); the summary is that Spark
[#269](https://github.com/MintPlayer/MintPlayer.Spark/pull/269) (`10.0.0-preview.53`) generates a
RavenDB index, a `V{Entity}` projection and `SparkContext` query roots from a `[GenerateIndex]`
attribute — and that it covers `Build`, `Repository` and `Account` but deliberately cannot cover
`Commit`.

Why it belongs in this document rather than only in the roadmap: two of the three surfaces this issue
added run on auto-indexes. `/api/uploads/status` resolves the repository by `FullName`
(`UploadsController.cs:307`) on every poll — 12/min per waiting job — and the cron that makes `state`
terminal at all (`FinalizeBuildsCronJob.cs:32`) filters three fields every sixty seconds forever.
Neither is wrong today; both are unmeasured.

**As built.** All four steps below landed, and step 5 — the generic-surface cutover, scoped out as
expensive — turned out not to be optional: registering a `[FromIndex]` projection reroutes the
entity's generic query automatically, so it happened the moment the attributes landed. That forced
three additions the plan had predicted as hazards: `Build.Run` became a stored field with a backfill
migration, `Repository.BadgeToken` got `[IgnoreForIndex]` (synchronize would have put it in the
anonymous grid), and the complex fields needed `FieldIndexing.No` because Corax faults on them —
filed as [Spark#273](https://github.com/MintPlayer/MintPlayer.Spark/issues/273), workaround in
`GeneratedIndexes.ComplexFields.cs`. Details in the "As built" section of
[adopt-generated-indexes.md](adopt-generated-indexes.md). 79/79 green; `--spark-verify-model` exit 0.

**Scope as planned** was steps 1–4 of that document:

1. `preview.52` → `preview.53`, nothing generated yet — the step that turns upstream-reading into
   compiled fact.
2. `[IgnoreForIndex]` on `Build.Run`, with a recorded decision on `Build.Sessions`. **Blocking**: the
   generator's membership test is opt-out, so a get-only computed property is otherwise emitted into
   a map that runs against a document where the field does not exist.
3. `[GenerateIndex]` on `Build` and `Account`; move `FinalizeBuildsCronJob`,
   `BuildActions.Commit_Builds` and the three `Account` lookups onto them.
4. `[GenerateIndex]` on `Repository`; move the six `FullName` lookups — including this issue's — and
   the two `OwnerLogin` ones.

**Out of scope**, and stated so it is not attempted by accident: the generic-surface cutover
(`CoverageSparkContext` → index roots) is step 5 there, is gated on SP4, and drags `App_Data/Model`,
`modelHashes.json`, `RepositoryVisibility.Filter`'s element type and the `Repository_Commits` default
sort with it. `Commit` is out of N6 — one index per entity is a compile error, and
`Commits_ByRepository`'s coalesced `AuthoredAt` is not expressible as a generated projection. Losing
that coalesce would silently mis-sort the *majority* of commits, since upload-only commits have no
webhook timestamp.

Two later routes exist and neither belongs here. Persisting `Date` and `HasCoverage` (step 6 there)
lets `Commit` be generated and deletes `Commits_ByRepository` outright, closing SP4 on the way.
Running a generated `VCommit` *alongside* the hand-written index is possible today by suppressing the
diagnostic, and is rejected: `IndexRegistry` binds a collection to whichever index
`Assembly.GetTypes()` reaches last, filed as
[Spark#272](https://github.com/MintPlayer/MintPlayer.Spark/issues/272) 🟩.

**Exit criteria**: the app builds and the suite is green on `preview.53`; `Build`, `Account` and
`Repository` each resolve through a named static index rather than an auto-index; `--spark-verify-model`
still exits 0; `Commits_ByRepository` is untouched; and the two conventions now in the codebase
(`OfType` for the hand-written index, `ProjectInto` for generated ones) are stated in the index
doc-comments rather than left for the next reader to infer.

---

## 9. Out of scope

- **Patch coverage** (M11.1) and **check runs** (M11.3) — unchanged, and unchanged in priority.
  Everything here is the interim surface that lets a consumer gate *before* those land, and it stays
  useful after: a workflow that wants the number for something other than a merge gate still needs it.
- **T1.2's partial-parse state** — SP3 only pins how it will map; the state itself is T1.2's.
- **Thresholds, `ignore`, config schema** — T1.5 / M11.2, now unblocked by §4.2.
- **A `/compare` or `/pulls` endpoint** — the 404s in the issue were exploratory, not requests.
