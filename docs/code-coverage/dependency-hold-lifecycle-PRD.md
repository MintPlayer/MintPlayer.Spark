# Dependency updates: hold lifecycle (cancelled runs, close, reopen, merge, `/unhold`)

Issue: #683. Branch: `fix/issue683-cancelled-checks-no-verdict`, one pull request (#684). Status: **draft, awaiting the owner's
answers in §5.** D-O29 (a cancelled check is no verdict) is implemented. Everything else here is proposed.

## 1. Why

On Spark #682 (2026-11-16), the bisect convicted `platform:dotnet 11.0`, the .NET 11 GA SDK and its companions, on a
CI run that had been **cancelled**, not failed (§2 of #683). The commit builds with 0 errors on SDK 11.0.100. The
conviction left two records behind, and neither has a way back:

- a `red:platform:dotnet 11.0` hold in the repository's `DependencyUpdateState`. `/unhold` refuses it, and closing the pull
  request does not touch it.
- `fleet.RedVersions` entries for every item of the group. These hold .NET 11 GA out of **every other repository of the
  account**, with no expiry and no command to withdraw them.

The owner closed #682 expecting a fresh start ("if we close #682 and recreate the migration pull-request, I hope it won't
be bothering us anyway", 2026-11-16). Today it would bother us: the next rolling PR would still leave the group out.

## 2. What exists today (audit 2026-11-16, file:line on master `561ef4901`)

Paths are under `apps/CodeCoverage/CodeCoverage/Dependencies/` unless stated.

| Record | Created | Lifts | Expires |
|---|---|---|---|
| Hold `blocker:<g>` | `Policy/DependencyHolds.cs:23-59` (apply result) | A new version token, or the tokens change (`:159-161`); `StandsFor` false (`:178`); an incomplete plan (`Policy/DependencyPolicyEngine.cs:662-675`) | 4 scheduled slots (`RetrySlots`, `:15`) |
| Hold `apply:<g>`, `migrate:<g>` (Unattributed) | engine apply/migrate results | The lookup fingerprint changes (`:162`) | 4 slots |
| Hold `red:<g>` (Unattributed) | `Lifecycle/DependencyUpdateEngine.cs` `CulpritAsync` | The fingerprint changes | 4 slots |
| Hold `human:<dep>` | `DependencyHolds.cs:65` (a human manifest edit, Q11) | A new version token, or `/unhold` | never |
| Hold `command:<x>` | `/hold` (`DependencyHolds.cs:91`) | `/unhold` only (`:111`) | never |
| Approval | `/approve` (`DependencyHolds.cs:131`) | Goes stale when the group, deps, majors or coverage.yml change | never |
| `fleet.RedVersions` (Q15, PRD §7.7) | `CulpritAsync` only (`RecordFleetRedAsync` has no caller) | **nothing** | trim at 2000 entries |
| `run.Pins` | human fixes (D-N1) | when the pull request ends | per run |
| `Bisect.Excluded` | each bisect step | `/bisect`, a culprit, a refresh | per run |

What else the audit found:

- **`/unhold` refuses bot holds** (`DependencyUpdateEngine.cs` `RemoveCommandHold`: "the bot's own holds lift by
  themselves"). PRD line 1220 states the same.
- **What the fleet red list blocks:** a candidate whose ecosystem, dependency and version match an entry recorded by
  *another* repository is reported `FleetRed` and is not planned (`DependencyPolicyEngine.cs:163-165, 1146`). The list also
  ranks bisect suspects (`DependencyLifecycleRules.cs:347`), blocks migration bumps (`DependencyHolds.cs:240-265`) and
  shows in the fleet view (`DependencyFleetView.cs:230`).
- **The PRD's own wording isn't implemented:** "a version that goes red ... is held for repos that haven't merged it yet".
  Nothing prunes an entry when a repository merges that version.
- **Close** (`PullRequestEndedAsync`) clears the pull request number, the shas, the pins and `RollingPlanJson`, and ends a
  run that was waiting for checks or bisecting. It does not touch holds, approvals or the fleet. It **does not delete the
  rolling branch**; only offboarding does (`:1856`).
- **Reopen:** nothing handles it. GitHub's `pull_request.reopened` is not normalised (`GitHubEventsRecipient.cs:545` raises
  closed only). After a close, `run.PullRequestNumber` is null, so the bot no longer tracks the reopened pull request, and
  the next trigger tries to open a second one from the same branch.
- **Merge** takes the same path as close (`wasMerged: true`).
- **Commands:** `/approve`, `/hold`, `/unhold`, `/bisect`, `/refresh` (`CodeCoverage.Library/Forge/DependencyCommandParser.cs`,
  enum `ForgeEvents.cs:325`). Adding or changing one touches:
  - the parser, the enum and the engine `case`;
  - the PR body help text (`Lifecycle/DependencyPullRequestBody.cs:180`);
  - `docs/code-coverage/dependency-updates.md` (commands table), `hosting.md:441`, `forge-webhooks.md:92`;
  - the in-app setup page (`ClientApp/src/app/pages/dependency-updates-setup/*.ts`, whose spec asserts the list);
  - the tests in `DependencyLifecycleRulesTests` and `DependencySagaTests`.

## 3. Principle

**A bot hold is evidence, and evidence belongs to the pull request it was gathered on.** A `red:` hold says "with this
group, this pull request's CI went red". When a writer rejects that pull request (closes it), or overrides the verdict
(`/unhold`), the evidence goes with it. That includes what the fleet learned from it. Human decisions (`/hold`, a
human's manifest value, `/approve`, `coverage.yml`) are never undone by the bot.

## 4. Proposed decisions

| # | Proposal | Why |
|---|---|---|
| H1 | **A cancelled check is no verdict** (= D-O29, implemented). Nothing is bisected, held or marked red on it. A superseded commit is left alone. Otherwise its checks are re-run once, and a second cancellation is reported without blame. | #683 |
| H2 | **Every bot verdict records where it came from.** `DependencyHold` gets `PullRequestNumber` and `RunUrl`. `DependencyFleetRed` gets `PullRequestNumber`; it already has `Repository`, `RunUrl`, `AtUtc`. | Without the link, close and `/unhold` cannot find what to withdraw. |
| H3 | **Closing the rolling pull request without merging withdraws its bisect verdicts**: the `red:` holds taken on it and the fleet red entries this repository recorded from it. It keeps the human and command holds and the approvals. It also keeps `blocker:`, `apply:` and `migrate:` holds, because those come from the lookup or apply, not from that pull request's CI, and they lift by fingerprint. | §3. The owner's expectation on #682. |
| H4 | **`/unhold <group>` also lifts a bot hold on that group** (`red:`, `blocker:`, `apply:`, `migrate:`). For a `red:` hold, it also withdraws this repository's fleet red entries for that group's items. It answers 🎉 and refreshes, as for a command hold. The refusal text goes. | The writer overrides the bot's evidence. One verb for "let this group in again"; see Q2 for a separate verb instead. |
| H5 | **Reopening the rolling pull request adopts it again.** `pull_request.reopened` is normalised and handled: `run.PullRequestNumber` is set back when the head branch is the rolling branch and no other rolling pull request is open, then a Refresh trigger rebuilds it, replaying human commits as always. What H3 withdrew stays withdrawn. | Today a reopened pull request is orphaned and a second one would be attempted. |
| H6 | **A merge prunes the fleet**: entries this repository recorded for a dependency at a version that the merged plan applied are removed, because the version works here now. That implements the PRD's "haven't merged it". | §2 |
| H7 | **Fleet red entries expire** after N days (Q4), so a stale verdict cannot block the fleet forever. | §2 |

## 5. Open questions for the owner

| # | Question | Recommendation |
|---|---|---|
| Q1 | Does closing the rolling PR withdraw only `red:` holds and fleet entries (H3)? Or all bot holds, so that close means a clean slate? | `red:` only. The others are not that pull request's evidence, and they already lift on their own. |
| Q2 | Should H4 extend `/unhold`, or add a new verb (`/retry <group>`, `/clear <group>`) and keep `/unhold` for human holds? | Extend `/unhold`: one verb, nothing new to learn. |
| Q3 | Reopen (H5): adopt and refresh, or close it again with a comment that the bot opens its own? | Adopt and refresh. |
| Q4 | Fleet red expiry (H7): how many days, or none? | 14 days. A real breakage is found again by the next red bisect. |
| Q5 | The stale #682 records in production: removed by hand in the database after the deploy (owner approved), or by `/unhold platform:dotnet 11.0` on the new rolling PR once H4 ships? | `/unhold` once H4 ships. It exercises the feature, and it needs no database edit. |

## 6. Spikes (live, `PieterjanDeClippel/DependencyUpdaterTest`, local app)

**Setup.** Run the local CodeCoverage app (`dotnet run --launch-profile https`) with the smee channel
(`GitHub:SmeeChannelUrl`) and the `GitHub:Development:*` App keys in user secrets, against local RavenDB
`localhost:8080`, database `Coverage`. Webhooks for repositories with the **CoverageDevelopment** App installed reach it.
DependencyUpdaterTest has both Apps installed, so **turn its dependency updates off in production** for the duration,
otherwise both bots act on the same repository.

**Evidence format** follows §13.13: each step with a timestamp, PR number and sha. Defects get numbered and become
decision rows, with red → green test names.

| # | Spike | Question it answers |
|---|---|---|
| S0 | A deterministic red: a CI step that fails when a chosen package is at a chosen version (e.g. `grep` the lockfile in `ci.yml`). Add `concurrency: cancel-in-progress` to `ci.yml` too. | How we make a group convict on demand, and reproduce the #682 cancellation. |
| S1 | Push on top of the bot's commit while its CI runs; also `gh run cancel` the bot's run by hand. | H1 live: no bisect, the superseded note, one re-run, the second-cancel report. |
| S2 | Let S0 convict a group, then close the rolling PR. | H3: the `red:` hold and the fleet entries are gone, and the next run includes the group again (and convicts it again, since S0 still breaks). |
| S3 | Reopen the closed PR. Also reopen it after the bot has already opened a new one. | Does GitHub send `reopened`, and with which head sha? Does GitHub allow reopening while another PR is open from the branch? Does the branch survive a close? Then H5 live. |
| S4 | `/unhold <group>` on a `red:` hold. | H4: hold and fleet entries withdrawn, 🎉, refresh. |
| S5 | Merge a plan that holds a version another repository marked red. | H6: the pruning. This needs a second test repository on the dev App, or a seeded fleet entry from a fake repository id. |

## 7. Plan (milestones, one PR #684)

| M | What | Tests |
|---|---|---|
| M1 ✅ | H1 / D-O29 | `DependencySagaTests.Issue683_*` (3) |
| M2 | H2: the provenance fields, written by `CulpritAsync` | the saga test asserts the PR number on the hold and on the fleet entry |
| M3 | H3: close withdraws | saga: convict → close → no hold, no fleet entry, human/command holds kept; merge is not close |
| M4 | H4 (per Q2): `/unhold` on bot holds; help text, `dependency-updates.md`, `hosting.md`, `forge-webhooks.md`, setup page and its spec | saga `R6_*`; parser `InlineData` if Q2 picks a new verb |
| M5 | H5: normalise `reopened` (GitHub), recipient, engine adopt + refresh | `GitHubEventsRecipient` test, recipient test, saga reopen test; reopen with another PR open is refused |
| M6 | H6 + H7: merge prunes, expiry | `DependencyStateStore` / policy tests |
| M7 | Spikes S0–S5 on the dev App; the results go into §8 | live |
| M8 | Docs: decision rows in `dependency-updates-PRD.md` (D-O30+), the user guide's commands and holds sections | |

## 8. Spike results

_Not run yet._
