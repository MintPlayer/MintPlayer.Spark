# SpaServices: first release from `release/dotnet-10.x` (tracking PRD + plan)

Working log (owner, 2026-11-13: "just to keep track of what we're doing"); committed with #653. Follows #649 / PR #650 (merged
2026-11-12 21:27 UTC as `81907805`, deployed). Parent plan: `docs/lockfile_notice_and_line_cut_PRD.md` §4 "After deploy
(M7-M13), the D17 acceptance on SpaServices".

**Goal:** MintPlayer/MintPlayer.AspNetCore.SpaServices releases `10.x` packages from `release/dotnet-10.x` while master
is on .NET 11, and backports reach that line.

## 1. State (2026-11-13, ~07:05 UTC)

| Item | State | Evidence |
|---|---|---|
| master | `16141c1` + #112 merged | #108 squash (`16141c1`), then #112 |
| `release/dotnet-10.x` | `16141c1`, unnarrowed: TFMs `net10.0;net11.0`, `global.json` SDK 11 rc.1, `LangVersion 15` | GitHub branches API; `global.json:3` |
| `release/dotnet-11.x` | `0f31c1e` (#105), lacks #108 and #112 | commit graph on the repository page |
| #112 `mintplayer/onboarding` → master | **merged** (queued 07:0x) | 4 `setup-dotnet` steps → `global-json-file: global.json` |
| #114 `mintplayer/narrow-dotnet-10.x` → 10.x | **open, red** | see §2 D1, D2 |
| #113 `mintplayer/narrow-dotnet-11.x` → 11.x | **open, Line policy red** | see §2 D3 |
| #111 dependency updates → master | open (draft), unrelated | |

### Why master == `release/dotnet-10.x` is not a bug

Asked by the owner. The older line is cut from master's head on purpose while master still multi-targets
(`net10.0;net11.0`); the narrowing pull request then makes it .NET 10 (`docs/code-coverage/versioning.md:366-383`).
`16141c1` shows `coverageproduction[bot]` as author because it is the squash of the bot-opened onboarding PR #108, which
the owner merged. Moving branch pointers by hand is not needed and would bypass the rulesets.

## 2. Defects / findings

**Status (2026-11-13 ~08:40):** D1 ✅ and D3 ✅ (Spark #652, deployed; #114 green with `LangVersion 14`, no PR into
11.x) · D2 ✅ (#115 → backport #116) · D5 ✅ (#117, #118 merged) · D7 ✅ (#119 → backport #120; 10.8.1 on nuget.org) ·
D4 ⏳ (Spark #654 merged and deployed; its follow-up #655 is PR #656, queued 2026-11-13; then `refresh` on #111) · D6 ✅
(hint in "Backport targets", Spark #652).

| # | Finding | Evidence | Owner of the fix |
|---|---|---|---|
| D1 | The narrowing leaves `<LangVersion>15</LangVersion>` (C# 15 = .NET 11) in every csproj; the .NET 10 compiler refuses it. | #114 run 43382800307, node 20/24: `CSC : error CS1617: Invalid option '15' for /langversion` | **Spark** (`LineNarrowing`): lower LangVersion to the line's C# version (.NET 10 → 14) |
| D2 | NU1107 on the line: EF Core Design 10.0.15 pins `Microsoft.CodeAnalysis.CSharp.Workspaces` 5.0.0 (= `Common` 5.0.0), while `MintPlayer.SourceGenerators 10.22.0` → `MintPlayer.ValueComparers.NewtonsoftJson 10.20.2` → `MintPlayer.SourceGenerators.Tools 10.21.0` → `Microsoft.CodeAnalysis.CSharp 5.3.0`. Master/11.x don't conflict (EF 11 rc also wants Roslyn 5.3). | #114 run 43382800307, `pull-request` job, `Demo.Data`/`Demo.Web`; nuspecs on nuget.org | **SpaServices**: update `MintPlayer.SourceGenerators` + `.Attributes` to ≥ 12. Not a Tools bug any more: `ValueComparers.NewtonsoftJson` dropped the Tools dependency in 12.0.0 (Tools commit `19e810c06`, 2026-09-26, #187); 12.0.0-13.0.0 depend on `Newtonsoft.Json` only. **No Tools issue filed** for that reason. |
| D3 | The bot opens a conversion-only pull request into the **current** line (#113, D16 "a line that needs no narrowing gets that pull request for the conversion alone"), and its own Line policy check refuses it: "`release/dotnet-11.x` is the current dotnet line: everything on it comes from `master`… Open this pull request against `master` instead". | #113 check run "Line policy" | **Spark** (`OnboardingService.NarrowAsync`): skip the current line; it gets #112 by backport |
| D4 | The dependency updater holds `MintPlayer.SourceGenerators`: "the resolver refused it: blocked by MintPlayer.SourceGenerators.Attributes (R2)". Real constraint or updater defect? | repository page, Dependency updates → Held | **Diagnosed 2026-11-13 → Spark #653** (`docs/dependency_blocker_hold_PRD.md`). Real constraint, wrong grouping: `MintPlayer.SourceGenerators` 13.1.0's nuspec requires `MintPlayer.SourceGenerators.Attributes` ≥ 13.1.0, but the two land in **separate groups** (`GroupOf`, `DependencyPolicyEngine.cs:79-83`: no configured group, no Renovate `groupName`). The Generators group was applied alone against Attributes 10.20.1 → restore error, correctly attributed to Attributes (apply run 42571592349, 2026-11-09; #111 "Held" ×3: 12.1.1, 12.2.0, 13.1.0), while Attributes 10.20.1 → 13.1.0 waits for `approve` in its own group. Local restore with both at 13.1.0 is clean (master today and at `0f31c1e`). Second defect: `DependencyHolds.Lifts` only lifts on a version token not seen before, and `Seen` mixes current and candidate versions, so approving Attributes (current → 13.1.0, already seen as a candidate) does **not** lift the hold; only the 4-slot retry does. |
| D5 | #108 and #112 were never backported to `release/dotnet-11.x` (bot PRs get no `backport:` labels, `backports.md:112`). | branch heads | **SpaServices**: owner labels/`/backport` |
| D7 | Line release packed `11.0.0-rc.5` instead of `10.8.0`: SpaServices pinned `MintPlayer.Versioning.MSBuild` 1.0.1 (no major filter, MPV006 first in 1.3.0). | run 43390241308, "Check the packages carry the released versions" | **SpaServices**: #119 → backport #120; run 43392899994 published **10.8.1**. No issue exists for it. |

## 3. Decisions

| # | Decision | Why |
|---|---|---|
| A1 | Don't hand-move `release/dotnet-10.x` / `release/dotnet-11.x`. | Layout is by design (§1); rulesets restrict updates/deletions. |
| A2 | The package bump (D2) lands on **master** and keeps its `backport: release/dotnet-10.x` label (and 11.x). Removing the 10.x label (owner's first idea) would leave #114 red. | `backports.md:107-113`: a removed label means "don't take it to that line"; the conflict exists only on 10.x. |
| A3 | Claude does not commit to SpaServices; the owner (or the updater) opens that PR. | Global rule: other repos via issue, never edit/commit from here. |

## 4. Plan

1. ✅ #112 merged; narrowing PR #114 opened by the merge (step 6 of `onboarding.md`).
2. ⏳ **Owner:** open the SpaServices PR into master: `MintPlayer.SourceGenerators` + `.Attributes` → ≥ 12 in
   `Demo/Prerendering/Demo.Data` and `Demo.Web` (D2). Keep both `backport:` labels; queue it.
3. ⏳ **Claude, Spark PR (new issue):** D1 (LangVersion narrowing) + D3 (no conversion-only PR into the current line).
   Red test first, update `docs/code-coverage/onboarding.md` step 6 and `versioning.md`, record evidence in
   `docs/lockfile_notice_and_line_cut_PRD.md`.
4. After 2's backport is on `release/dotnet-10.x` and 3 is deployed: rerun **Set up features** on SpaServices →
   `mintplayer/narrow-dotnet-10.x` rebuilt on the new line head (also fixes the shared `<ItemGroup>` overlap) → #114
   green → queue.
5. Close #113; backport #108 and #112 to `release/dotnet-11.x` (D5).
6. The line's versioning run tags `10.x.y`, packages publish. Earlier `plan-refused` runs on 10.x (2026-11-12 19:23,
   20:06, 20:25, `build-master.yml@refs/heads/release/dotnet-10.x`) should stop.
7. One real fix on master with `backport: release/dotnet-10.x` → publishes the next 10.x (D17 step 4).
8. Then S7, S9, S10 and the rest of S11 from the parent PRD; D4.

## 5. Log

- 2026-11-13 06:55 — Set up features (with **SDK uit global.json**) on SpaServices; GitHub sudo passkey by the owner;
  rulesets "set up already"; plan: 3 workflows on master. `coverage.yml` note: `versioning.lines` exists, so the bot
  left it (current lines take backports).
- 2026-11-13 ~07:00 — #112 approved by the owner, `/queue`d, merged; #113 and #114 opened.
- 2026-11-13 ~07:05 — #114 red (D1, D2); #113 Line policy red (D3). Tools issue not filed (D2: already fixed upstream).
- 2026-11-13 ~07:25 — Owner opened #115 (SourceGenerators + .Attributes → 13.1.0), queued, then cancelled the
  `pull-request` run for review. Review: package graph clean (13.1.0 nuspecs: no Roslyn deps); generators ship only
  under `analyzers/dotnet/roslyn5.9`, and 10.0.404 = VS 18.11 (releases.json), so they load if Roslyn 5.y tracks VS 18.y
  (inference; checked locally only for 10.0.114 → Roslyn 5.0.0). **Blocker:** the label is `release/dotnet-10.x`, but
  the backport reads `backport: <line>` (`BackportTargets.LabelPrefix`, `BackportRecipients.cs:130`, line name
  `dotnet-10.x`), so nothing would reach 10.x. "Backport targets" said "No line applies… changes no package" (demo
  projects aren't packages) → D6: a change the line needs to *build* gets no default label.
- 2026-11-13 07:33 — #115 had already merged (label unused). `/backport dotnet-10.x` on #115 → bot opened #116,
  handed it to the queue; all checks green (pull-request, node 20/24, Line policy, Merge queue) → merged as `5101c2c`
  on `release/dotnet-10.x`. Plan step 2 ✅; D2 fixed on the line.
- 2026-11-13 — Spark issue #651 (D1, D3, D6) → PR #652 (`fix/issue651-line-narrowing-langversion`, `477431f25`), red-first
  tests, 52 green locally. Plan step 3 in review. D6 resolved as a hint in the "Backport targets" check, not new labels.
- 2026-11-13 07:49 — #652 merged (`c217c254`), deployed (code-coverage-deploy 43388522826 green). Plan step 3 ✅.
- 2026-11-13 07:59 — Set up features rerun (SDK uit global.json; OAuth consent by the owner; no file changes on master).
  `narrow-dotnet-10.x` rebuilt as `980bd73` on `5101c2c`: TFMs `net10.0`, `LangVersion 14`, global.json 10.0.404,
  workflows `global-json-file`. `narrow-dotnet-11.x` untouched (D3 ✅). #114 **all green** (build-any, pull-request,
  node 20/24, Line policy; runs 43389706384, 43389713250). Plan step 4: needs the owner's approval, then `/queue`.
- 2026-11-13 08:03 — #114 approved by the owner, queued, merged (`5b16c44`). Plan step 4 ✅. #113 closed (D3 comment);
  `/backport dotnet-11.x` on #108 → #117, on #112 → #118, both handed to the queue (D5).
- 2026-11-13 08:04 — **D7:** the line's first release run (build-master 43390241308) tagged `…/v10.8.0` for all six
  packages, then **failed** at "Check the packages carry the released versions": `dotnet pack` produced
  `11.0.0-rc.5`. Cause: SpaServices pins `MintPlayer.Versioning.MSBuild` **1.0.1** (`Directory.Build.props:3`), which
  takes the highest reachable tag (master's pre-cut `11.0.0-rc.5`) whatever the line's major. The major filter (MPV006,
  Spark `3f3829c5a`, #639) first shipped in **1.3.0** (checked: v1.0.1-v1.2.1 tags lack that commit). #111 only goes
  to 1.1.0. Nothing was published (the check stopped the run). **Fix (owner, SpaServices):** PR into master
  bumping it to 1.3.0, with `backport: dotnet-10.x`; the line's next run then packs from the `10.*` tags.
- 2026-11-13 08:05 — #117 (#108 → `release/dotnet-11.x`) merged. #118 (#112 → 11.x) still open, its checks running
  (build-any, pull-request, node 20/24, Merge queue). D5 half done.
- 2026-11-13 ~08:20 — Owner's #119 (Versioning.MSBuild 1.0.1 → 1.3.0, `backport: dotnet-10.x`) reviewed, `/queue`d,
  merged; bot backport #120 → merged on `release/dotnet-10.x`. Release run 43392899994 **green**: tagged `…/v10.8.1`,
  packed all six packages at **10.8.1** and pushed them ("Your package was pushed"). **D7 fixed; goal reached.**
  10.8.0 stays tag-only (never published). Plan step 6 ✅.
- 2026-11-13 ~08:40 — Verified: 10.8.1 listed on nuget.org (flat container); #118 merged (D5 ✅). #115's backport came
  from the `/backport dotnet-10.x` comment, not its `release/dotnet-10.x` label (the bot only reads `backport: <line>`,
  then adds `backported: <line>`); that label is inert. #119's body says "Closes #117" (a merged backport PR, not an
  issue) and "should publish 10.8.0" (it was 10.8.1); no D7 issue exists in SpaServices.
