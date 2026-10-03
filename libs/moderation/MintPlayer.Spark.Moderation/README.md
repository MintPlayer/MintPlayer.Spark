# MintPlayer.Spark.Moderation

Community moderation for MintPlayer.Spark (#460, item 12): votes and a reputation ledger on your own
entities, privileges earned as `security.json` groups, ten vote-fraud defences, flags and a review
queue, locks, suspensions and an audit log.

- `MintPlayer.Spark.Moderation.Abstractions` — `IModeratable`, `ModerationRights`, `ReputationEventKinds`.
  Reference it from Domain/Library projects.
- `MintPlayer.Spark.Moderation` — the host package. References `MintPlayer.Spark`,
  `MintPlayer.Spark.Authorization` (accounts, lockout, the GDPR hooks) and `MintPlayer.Spark.Cron`.
- UI: `@mintplayer/ng-spark/moderation` (vote widget, flag button, review queue, reputation badge,
  moderator panel).

Moderation's data are plain RavenDB documents behind `/spark/moderation/*` (T6), not persistent
objects: an application does not own or sync a model for them.

## Setup

```csharp
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.AddAuthentication<SparkUser>();
    spark.AddSoftDelete();   // optional: restore / purge for moderators
    spark.AddHistory();      // optional: revert for moderators
    spark.AddMessaging();    // required: the vote reversal is a durable after-commit hook
    spark.AddModeration<SparkUser>();
});
```

```csharp
public class Question : IModeratable
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";
    public string? AuthorId { get; set; }          // stamped by Moderation, immutable after create
    public DateTimeOffset? PostedAt { get; set; }  // stamped by Moderation
}
```

`AuthorId` and `PostedAt` belong to the framework: the interceptor stamps them on create (current user
id, now) and restores the stored values on every later write, whatever the client posted —
otherwise anyone could collect the reputation of someone else's post. Ids only, never names (D8).

A vote never touches the target document (its etag would move and an author editing at the same
time would get a 409): scores live in `ModerationTallies/{targetId}`.

## Configuration: `App_Data/moderation.json` (D14)

The file holds the **contents** of `Spark:Moderation`. `AddModeration` inserts it as the
**lowest-precedence** configuration source (for a `WebApplicationBuilder`; otherwise call
`configuration.AddSparkModerationFile()`), so appsettings, user secrets, environment variables and
the command line override any value: `Spark__Moderation__Fraud__MaxVotesCastPerDay=10`. Code
passed to `AddModeration(o => …)` sets defaults that configuration then overrides.

```json
{
  "Reputation": { "UpvoteReceived": 10, "DownvoteReceived": -2, "DownvoteCast": -1, "FlagUpheld": 2, "FlagDeclined": 0 },
  "DownvoteCastTypes": [ "Answer" ],
  "ReverseVotesOnModeratorDelete": true,
  "Privileges": {
    "Upvote":   { "GroupId": "…", "Rep": 15,   "MinAccountAgeDays": 0,  "MinActiveDays": 0,  "Grants": [ "Vote" ] },
    "Flag":     { "GroupId": "…", "Rep": 15,   "MinAccountAgeDays": 1,  "MinActiveDays": 1,  "Grants": [ "Flag" ] },
    "Downvote": { "GroupId": "…", "Rep": 125,  "MinAccountAgeDays": 7,  "MinActiveDays": 3,  "Grants": [ "Downvote" ] },
    "Review":   { "GroupId": "…", "Rep": 500,  "MinAccountAgeDays": 30, "MinActiveDays": 10, "Grants": [ "Review" ] },
    "Edit":     { "GroupId": "…", "Rep": 2000, "MinAccountAgeDays": 60, "MinActiveDays": 20, "Grants": [ "Edit" ] }
  },
  "Earnable": [],
  "Fraud": { "MaxVotesCastPerDay": 30 },
  "NewAccounts": { "AccountAgeDays": 7, "MaxPostsPerDay": 5 },
  "Jobs": { "CreditingSchedule": "*/5 * * * *", "DetectorSchedule": "17 3 * * *" }
}
```

Startup validates the **layered** result against `security.json` and refuses to start on:
an unknown reputation event name; an `Earnable` entry on the destructive list; a privilege whose
group does not exist, is well-known (`anonymous`/`authenticated`), holds a right that is not
earnable, or has no grant at all.

**Earned privileges are bounded (D12).** A reputation-earned group may hold `Query`, `Read`, `New`,
`Edit` (reversible through History), `Vote`, `Downvote`, `Flag`, `Review`, plus what `Earnable`
adds. `Lock`, `Suspend`, `Audit`, `Purge`, `Restore`, `Revert` and `ViewDeleted` are **never**
earnable, not even through `Earnable`.

`dotnet run -- --spark-init-moderation` prints the `security.json` rights to add for each privilege
(`Grants` × every `IModeratable` type in `App_Data/Model`) and for a moderators group. It writes
nothing:

```csharp
if (builder.InitializeSparkModerationIfRequested(args)) return;
```

## Rights (no wildcards, D3)

| Right | Target | Allows |
|---|---|---|
| `Vote` / `Downvote` / `Flag` | each moderatable type | up-vote / down-vote / flag |
| `Lock` | each moderatable type | lock and unlock; holders are exempt from locks |
| `Review` | `Moderation` | review queue, case detail, decisions |
| `Suspend` | `Moderation` | suspend, unsuspend, merge sock puppets; merge/suspend decisions |
| `Audit` | `Moderation` | the audit log |

Privileges are applied as groups by a composed group-membership provider (core's
`AddGroupMembershipProvider`, request-cached): the ids come back from `IGroupIdMembershipProvider`.

## Votes and the ledger

- One vote per voter and target: the id is `ModerationVotes/{voterId}/{targetId}`; the vote, its
  ledger entries, the tally and the cap counters are one optimistic-concurrency transaction (spike
  S-MOD-E), retried on a fresh session when a concurrent vote won.
- Changing or withdrawing a vote writes a `Retraction` compensation; nothing in the ledger is ever
  edited or deleted except the `Credited` / `CompensatedById` flags.
- Voting on your own post, on a locked post, or while suspended is 400. A target the caller cannot
  see, that does not exist, or whose type is not `IModeratable` is the standard refusal (404, or 401
  when signing in would help) — byte-identical, so the vote and flag endpoints never reveal a hidden
  post (#453).
- Reputation is summed by hand-written map-reduce indexes (`Moderation/ReputationCells`,
  `Moderation/PendingReputation`, `Moderation/VotePairs`, and the map index
  `Moderation/EntriesToCredit`), deployed from this assembly by core; a test asserts they deploy
  (a deploy failure only logs to the console). The privilege provider reads a per-user
  `ModerationReputation/{userId}` summary document, recomputed from the indexes by the crediting job
  and by every reversal, flag decision and deletion — so a request costs one batched load, never a
  query. A vote does not recompute (it would cost the vote path two index waits); the badge's
  "+N pending" is therefore read live from `Moderation/PendingReputation` by
  `GetReputationAsync`, so a new vote shows at once.
- A reversal of an entry that is already credited is itself credited at once (a decision, like a
  flag outcome), so the total drops when the reversal recomputes the summary, not at the next
  crediting run. A reversal of a still-pending entry, and every retraction, waits for the entry it
  cancels.

## The ten vote-fraud measures (D14)

Every threshold is under `Spark:Moderation:Fraud` (or `NewAccounts`) and overridable.

| # | Measure | Keys (defaults) |
|---|---|---|
| 1 | Every privilege has a reputation, account-age and active-days gate. | `Privileges:*:Rep`, `MinAccountAgeDays`, `MinActiveDays` |
| 2 | Diversity: vote-derived reputation counts toward privileges only from ≥ `max(3, votes/5)` distinct voters over ≥ `votes/4` distinct days; otherwise only non-vote reputation counts. | `DiversityMinVoters` 3, `DiversityVotersDivisor` 5, `DiversityDaysDivisor` 4 |
| 3 | A voter younger than 7 days or under 50 reputation changes the score but gives 0 reputation. | `EligibleVoterMinAgeDays` 7, `EligibleVoterMinReputation` 50 |
| 4 | Caps: 200 rep/recipient/day from votes; 30 votes cast/voter/day (429); a voter→author pair is credited 3×/day and 10×/30 days. | `MaxReputationPerRecipientPerDay`, `MaxVotesCastPerDay`, `MaxCreditedPairVotesPerDay`, `MaxCreditedPairVotesPer30Days` |
| 5 | Delayed crediting: a vote's entries count after `CreditableAfterUtc = cast + 48 h`, credited by a Cron job (a flag written with the vote, not a delayed message). | `CreditDelayHours` 48, `Jobs:CreditingSchedule` |
| 6 | Nightly detector over 30 days: serial (≥ 5 A→B in 24 h) and concentration (≥ 10 votes, ≥ 50 % on one author) are **reversed automatically**; reciprocal voting, fast voting (< 60 s after the post) and registration clusters (accounts created within 1 h sharing a network hash or a non-webmail domain, voting for each other) **open a review case only** (NAT false positives). | `DetectorWindowDays`, `SerialVotesIn24Hours`, `ConcentrationMinVotes`, `ConcentrationPercent`, `ReciprocalMinVotesEachWay`, `FastVoteSeconds`, `FastVoteMinCount`, `RegistrationClusterMinutes`, `WebmailDomains`, `Jobs:DetectorSchedule` |
| 7 | A reversal is a compensating `Reversal` entry `{RuleId, CaseId}`, one per original entry (idempotent by id), never a deletion. The recipient sees "Voting corrected (−N)" without the voters. Merging or deleting an account reverses every vote it cast. | — |
| 8 | Review surface per case: voter→target matrix, timeline vs post creation, account ages and registration methods side by side, "shares a network hash with N accounts" (never the address), reputation before/after, the rule fired; decisions dismiss / reverse / merge / suspend, each audited. | — |
| 9 | Only an HMAC-SHA256 of the truncated address (/24 IPv4, /48 IPv6) is stored, under a key rotated every 30 days and kept Data-Protection-protected; observations expire after 90 days. | `IpKeyRotationDays` 30, `IpObservationRetentionDays` 90 |
| 10 | `SparkUser.CreatedAtUtc` and `RegistrationMethod` (#460 M5, backfilled) feed the age gates and the review surface. An account with no known creation time is treated as brand new. | — |

Out of v1: device fingerprinting, ML/graph Sybil detection, vote fuzzing, shadow bans, automatic
suspension. **Limit:** a patient attacker still farms slowly — the thresholds are visible
configuration. What bounds the damage is that only reversible privileges are earnable.

## Flags, review, locks, suspensions

- `POST /spark/moderation/flag { objectTypeId, id, reason }`: one open flag per user and post,
  joined into `ModerationCases/flag/{targetId}`. Upheld: each flagger `FlagUpheld` (+2, credited at
  once); declined: `FlagDeclined` (0).
- **Lock** (`Lock/T`): `ModerationLocks/{targetId}`. For everyone without `Lock/T` a save, AsDetail
  change, custom-action write, revert, delete, restore and purge of the post is **400 "This post is
  locked."**, and the detail page loads without Edit/Delete. A module **sync** passes (the owner
  already decided). `/spark/po/delete-row` writes nothing, so it is not refused — the parent save is.
- **Suspend** (`Suspend/Moderation`): writes `ModerationSuspensions/{userId}` first — read by id on
  every request, so the write block and the loss of reputation groups apply to the **next request**
  — then Identity lockout + security-stamp refresh. Measured (spike S-MOD-C, 30-minute
  `SecurityStampValidatorOptions.ValidationInterval`, 1-hour bearer tokens): an existing **cookie**
  keeps authenticating until its next stamp validation (≤ the interval), a **bearer** access token
  until it expires (≤ 1 h); refresh and a new sign-in are refused at once. The server-side block
  covers that window: a suspended user can authenticate but cannot write.
- **New-account throttle**: an account younger than `NewAccounts:AccountAgeDays` creating more
  than `MaxPostsPerDay` moderatable posts in a UTC day gets **429** (`SparkThrottledException`,
  `Retry-After`), not 404.
- A moderator deleting (or purging) someone else's post reverses the votes it earned
  (`content-deleted`); an author deleting their own post keeps them. The reversal is a durable
  after-commit hook (`ModerationVoteReversal`): stored in the delete's own commit and run by Messaging
  with retries, so a committed delete always reverses its votes, shortly after.
- Every lock, unlock, suspension, merge, decision, automatic reversal, and moderator
  delete / purge / restore / revert of moderatable content is written to `ModerationAuditEntries`.

## Endpoints (`POST`, antiforgery, ids in the body)

`/spark/moderation/vote`, `/votes`, `/flag`, `/lock`, `/unlock`, `/status`, `/reputation`,
`/reputation/history`, `/cases`, `/case`, `/case/decide`, `/suspend`, `/unsuspend`, `/merge`,
`/audit`. Service API: `ISparkModeration`.

`ISparkModerationJobs` runs the Cron jobs' work on demand — `RunCreditingAsync` (crediting +
summary recompute), `RunFraudDetectorAsync`, `RecomputeReputationAsync(userIds)` — for an operator
after a threshold change, or an end-to-end test that must not wait for the schedule. It checks no
right (the jobs have no caller either) and is never mapped by the package; `apps/QnA` exposes it only
to its E2E host.

## GDPR (D8)

- `ISparkAccountDeletionHandler<TUser>`: reverses every vote the account cast, deletes its votes,
  flags, network observations, profile, summary and suspension, and anonymises the ledger (entries
  stay — other users' reputation is made of them — but name `deleted-user`). Idempotent.
- `ISparkPersonalDataContributor<TUser>` (`moderation` section): votes cast, flags raised,
  reputation, suspension.
- RavenDB revisions of moderation documents are not rewritten (see the authorization guide).

### Network observations: legitimate-interest assessment (template)

*Purpose.* Detect coordinated voting fraud (sock-puppet rings) that would otherwise let accounts
earn moderation privileges they did not earn. *Necessity.* Account age and voting patterns alone
cannot tell one person with five accounts from five people; a coarse network signal can, and no less
intrusive signal was found. *Minimisation.* Only an HMAC of the /24 (IPv4) or /48 (IPv6) network is
stored, never the address; the key rotates every 30 days and is protected by the application's Data
Protection key ring, stored outside the database; observations expire after 90 days; a match only
opens a case for a human, never an automatic sanction; reviewers see a count ("shares a network with
N accounts"), never a network. *Balance.* Users can reasonably expect a community site to protect
its voting from manipulation; the data cannot identify a person or a location on its own.

### Privacy-notice text (template)

> To protect voting on this site from manipulation, we keep a one-way, keyed fingerprint of the
> network you connect from (not your IP address) for 90 days. It is used only to spot groups of
> accounts that vote for each other from the same network, and a moderator reviews every such case.

## Tests

`tests/MintPlayer.Spark.Tests/Moderation/`: spikes S-MOD-A (indexes), S-MOD-C (suspension), S-MOD-D
(lock per write path), S-MOD-E (atomic vote), and one deterministic test per fraud measure on a
controllable `TimeProvider`.
