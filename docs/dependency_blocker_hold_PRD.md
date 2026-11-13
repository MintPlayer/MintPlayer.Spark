# #653: a blocker hold that waits for its blocker (PRD + plan)

Issue: [#653](https://github.com/MintPlayer/MintPlayer.Spark/issues/653). Branch: `fix/issue653-blocker-hold-lift`.
Found as D4 of `docs/spaservices_dotnet10_line_PRD.md` (2026-11-13). Area: CodeCoverage dependency updater (production).

## 1. Problem

On MintPlayer.AspNetCore.SpaServices the updater holds `MintPlayer.SourceGenerators` with "the resolver refused it:
blocked by MintPlayer.SourceGenerators.Attributes (R2)". The SpaServices dependency PR #111 lists it under "Held" for
12.1.1, 12.2.0 and 13.1.0, while `MintPlayer.SourceGenerators.Attributes` 10.20.1 → 13.1.0 sits under "Awaiting
approval" in its **own** group.

| # | Finding | Evidence |
|---|---|---|
| F1 | The two packages must move together: `MintPlayer.SourceGenerators` 13.1.0 depends on `MintPlayer.SourceGenerators.Attributes` ≥ 13.1.0. | nuget.org nuspec `mintplayer.sourcegenerators/13.1.0`, `<dependency id="MintPlayer.SourceGenerators.Attributes" version="13.1.0" …>` |
| F2 | They land in separate groups: `GroupOf` takes a configured group, else Renovate's candidate `groupName`, else the record's, else the package name. There is no rule and no Renovate monorepo preset for them. | `DependencyPolicyEngine.cs:79-83` |
| F3 | The `MintPlayer.SourceGenerators` group was applied alone and the restore failed. The attribution to Attributes is **correct**. | apply run 42571592349 (2026-11-09 06:12): "group MintPlayer.SourceGenerators: unresolvable"; SP1 below |
| F4 | Both at 13.1.0 restore cleanly. | local `dotnet restore` of a scratch clone, on SpaServices master today and at `0f31c1e` (master on 2026-11-09): exit 0, no NU errors |
| F5 | **The hold does not lift when the blocker moves.** `Lifts` (Blocker kind) needs a token in `Seen(lookup, blocker)` that was not there when the hold was taken. `Seen` unions current value, current version, locked version **and** candidates. After `approve MintPlayer.SourceGenerators.Attributes`, Attributes' current becomes 13.1.0, which was already in the set as a candidate, so nothing is new. Only `SlotsHeld >= RetrySlots` (4 scheduled slots ≈ 4 weeks on a weekly schedule) lifts it. | `DependencyHolds.cs:145-159` |
| F6 | The Held row says what blocks, not what unblocks: the blocker's own group is under "Awaiting approval" in the same body, unlinked. | `DependencyPullRequestBody.cs:72-77`, held entry text from `groupHold.Reason` (`DependencyPolicyEngine.cs:325`) |

Workaround available today (no code): an account rule or `coverage.yml` rule
`match: ["MintPlayer.SourceGenerators*"]`, `group: MintPlayer.SourceGenerators` (`DependencyPolicyConfig.ConfiguredGroup`).

## 2. Spikes

| # | Question | How | Result |
|---|---|---|---|
| SP1 | What exactly does NuGet report for F3, and what does our parser make of it? | Scratch clone at `0f31c1e`, only `MintPlayer.SourceGenerators` → 13.1.0 in `MintPlayer.AspNetCore.SpaServices.Xsrf.csproj`, `dotnet restore`; read `obj/project.assets.json` `logs[]`. | **Done (2026-11-13).** One log: `code NU1605`, `level Error`, `libraryId MintPlayer.SourceGenerators.Attributes`, message "Warning As Error: Detected package downgrade: MintPlayer.SourceGenerators.Attributes from 13.1.0 to 10.20.1 …". `parseNuGetAssets` gives `depName` = Attributes, `version` = **13.1.0** (`nugetVersionFromMessage`, the "from X" token = the version the planned package needs). So the blocker already carries the version that would unblock it; `DependencyBlockerSeen` drops it. |
| SP2 | Do any other repositories in the fleet hold a group on a blocker that is itself a planned/awaiting dependency? | Read production `DependencyUpdateStates` (holds with `Kind = Blocker`) against each repository's last report. Read-only; needs the owner's go-ahead (production read). | Open. Sizes the value of D2 (the merge), not needed for D1. |
| SP3 | Production holds were written without the new field (D1). What happens to them at deploy? | Read `Lifts` after the change. | **Decided otherwise:** an empty `CurrentTokens` means "not recorded", so the hold does **not** lift for it. SpaServices' standing hold then joins Attributes' group on the first run after the deploy (D2), with no wasted apply. Test `Issue653_D2_a_hold_written_before_653_…`. |
| SP4 | Does a merged group (D2) keep a standing approval valid? | Read `Approved`/`Outcome` (gap E): an approval binds the group name, its dep set and target majors. | **Answered:** the merged group keeps the blocker's name, so the `approve` comment shown is the blocker's group's; the approval binds both packages and plans both (`Issue653_D2_a_held_group_joins_…`). An approval given to the blocker's group **before** the join expires (its dep set changed) and is asked again with both listed, which is the change that needs approving. |

## 3. Decisions

| # | Decision | Why / evidence | Status |
|---|---|---|---|
| D1 | A blocker hold also lifts when its blocker's **current** version changes (current value, version and locked version across its package files, recorded per blocker as `CurrentTokens` when the hold is taken). The reported version is not stored: a moved current covers it. A hold written before #653 has no `CurrentTokens` and lifts only as before (no lift at deploy; refines SP3). | F5; test `Issue653_D1_…` and `…_a_hold_written_before_653_…` | ✅ Built |
| D2 | When a blocker hold's blocker has **its own group in the same evaluation**, the held group's members join the blocker's group (`DependencyPolicyEngine.JoinBlockedGroups`). The merged group keeps the **blocker's** group name and follows the normal rules (here: a major → `approve MintPlayer.SourceGenerators.Attributes` covers both). The hold **stays** in the state, because it is what keeps the two together, and its retry clock is reset on every join so the group never splits after 4 slots. It lifts by D1 once the merged group is applied (the blocker's current moves). | F1-F3; owner Q1 = A (2026-11-13: "A - Include D2"). Tests `Issue653_D2_…` (three). | ✅ Built |
| D3 | ~~The Held row names the unblocking step when the blocker waits for an approval.~~ **Dropped:** with D2, a blocker that has an update of its own always takes the held group in, so no Held row can point at an awaiting blocker. A blocker with no update keeps today's text, which is right ("held until a newer version of it appears"). | D2 | Superseded by D2 |
| D4 | No change to the dependency-action (TypeScript) parser: it already reports the right package and version (SP1). | SP1 | ✅ |

### Q1 (owner): learned grouping (D2), or only lift + hint (D1 + D3)?

- **A. D1 + D2 + D3 (recommended):** one `approve` updates both, in one pull request. Trap: a mis-attributed blocker
  (a parser error) would merge unrelated packages. That is bounded, because a merge needs the blocker to have its own
  candidate in the same run, and a failed merged group is held again.
- **B. D1 + D3 only:** smaller. A person approves Attributes, the next run lifts the hold (D1), and the run after that
  applies SourceGenerators. That takes two runs and two pull request updates for one logical update.

## 4. Milestones (one PR)

- **M0** ✅ Issue #653, branch, this PRD; SP1.
**Status:** M0-M2 and M4 ✅; M3 (D3) dropped as superseded by D2. Gate green (see Log).

- **M1** D1: `DependencyBlockerSeen` gets `CurrentTokens` and `Version`; `RecordApplyResult` fills them from the lookup
  and the apply result's blocker; `Lifts` lifts on a changed current, or a current at or past `Version`. Red test first
  (`GroupHoldTests`): the SpaServices shape (hold on SourceGenerators, Attributes current moves 10.20.1 → 13.1.0, which
  was already a candidate) lifts; an unchanged current does not.
- **M2** D3: Held-row hint. Red test: the Held detail names `approve MintPlayer.SourceGenerators.Attributes` when that
  group is awaiting approval.
- **M3** D2 (only if Q1 = A): learned grouping in `Evaluate`. Red test: the two records plan as one group that awaits
  one approval; after the approval both are planned.
- **M4** Docs: `docs/code-coverage/` dependency-updates page (R2 holds: when they lift; learned grouping); this PRD's
  results. Also carries the unrelated doc updates waiting on master: S7 in `docs/lockfile_notice_and_line_cut_PRD.md`
  and `docs/spaservices_dotnet10_line_PRD.md`.
- **Gate:** the affected test classes once at the end (`GroupHoldTests`, the engine and PR-body tests), then the PR.

## 4b. #655: the join planned a tier without the blocker (follow-up, 2026-11-13)

Issue: [#655](https://github.com/MintPlayer/MintPlayer.Spark/issues/655). Branch: `fix/issue655-joined-group-tier`.

| # | Finding | Evidence |
|---|---|---|
| F7 | After the #654 deploy the join happened, but the merged group planned **`MintPlayer.SourceGenerators` 12.2.0 alone** and the restore refused it again (NU1605 in `Demo/Xsrf/XsrfDemo.csproj`: 12.2.0 needs Attributes ≥ 12.2.0). | apply run 43409288822, `result.json` group `MintPlayer.SourceGenerators.Attributes` |
| F8 | Renovate reads SourceGenerators' current as **12.1.0** (value 10.22.0), so 12.2.0 is a *minor* for it; every Attributes candidate is a *major*. `EvaluateGroup` walks the tiers down: the major tier waits for `approve`, the minor tier holds only SourceGenerators, under the auto-merge threshold. | lookup run 43412333132 (`lookup.json` records), `DependencyPolicyEngine.EvaluateGroup` tier loop |
| F9 | The new hold is on the merged group, blocked by one of its own members: no other group to join, and D1 never fires because the blocker is held with it. Only the 4-slot retry lifts it, to fail again. | #111 "Held" after the 10:51 run |

| # | Decision | Why / evidence | Status |
|---|---|---|---|
| D5 | A group joined on a blocker plans only a tier in which the blocker moves too (`joinedBlockers`, checked in `EvaluateGroup`). The lower tiers are skipped, so the group waits for the approval of the tier that holds both. | F7, F8; test `Issue655_a_group_joined_on_its_blocker_…` on the real lookup's shape | ✅ Built |
| D6 | A blocker hold whose blocker was **not** in the refused plan but is now a member of the held group, with candidates, lifts in `JoinBlockedGroups`: the plan was incomplete, not unresolvable. Bounded: a refused plan that did include the blocker keeps its hold. | F9; test `Issue655_a_hold_on_a_plan_that_left_out_its_own_blocker_…` | ✅ Built |

Owner (2026-11-13): no change in Dotnet.Tools first: "Updating .net standard packages to the latest major version
shouldn't really be a problem".

## 5. Log

- 2026-11-13 — D4 diagnosed (F1-F6), issue #653 filed, branch created, SP1 run (NU1605 capture above).
- 2026-11-13 — Owner: Q1 = A ("A - Include D2 - Go ahead"). `BlockerHoldTests` (4 tests) written first; **red** on the
  original code: 4/4 failed (source changes set aside with `git diff` / `git checkout`, then reapplied). Green with D1 +
  D2: `CodeCoverage.Tests.Dependencies` namespace 534/534 passed.
- 2026-11-13 — #654 merged and deployed. SpaServices' first runs after it (10:27, 10:51) joined the groups, then planned
  SourceGenerators 12.2.0 alone and held the merged group on itself (F7-F9). Issue #655. Two tests on the real lookup's
  shape, **red** on #654's code (2/2: the plan was `MintPlayer.SourceGenerators@12.2.0`, no hold lifted). Green with D5 +
  D6: namespace 536/536. (The tests run at the production date: the kit's 2026-10-16 put 13.1.0 in its cooldown.)
