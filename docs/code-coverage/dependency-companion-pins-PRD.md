# Pins that follow a dependency: versions the bot can't see (SeoTester's Playwright)

Status: **M0-M7 done, M8 (pull request) open** (2026-11-15). Branch `feat/dependency-companion-pins`.

## 1. The problem (owner, 2026-11-15)

Spark's rolling dependency pull request #669 moved Playwright from 1.63.0 to 1.64.1 in two of the four places that
must agree, and its `build + verify` job went red (run 43715251776, job 132815264771:
`playwright-core equals the image: '1.63.0' !== '1.64.1'`, `tools/verify-seotester-deploy.test.mjs:107`).

| Place | Kind | Moved by the bot? |
|---|---|---|
| `apps/SeoTester/SeoTester/SeoTester.csproj` `Microsoft.Playwright` | NuGet package | yes |
| `apps/SeoTester/chromium/Dockerfile` `FROM mcr.microsoft.com/playwright:v1.63.0-noble` | Docker image | yes |
| same Dockerfile, `RUN … npm install … playwright-core@1.63.0` | a version inside a command | **no**: no Renovate manager reads it |
| `apps/SeoTester/docker-compose.yml` `image: ghcr.io/mintplayer/seotester-chromium:1.63.0` | the repository's **own** image, built by `seotester-deploy.yml` with the `FROM` version as its tag | **no**: the tag doesn't exist until the deploy builds it |

A person finished the move on the branch (`af7b3b90e`); the bot replays that commit on every rebuild
(`RollingBranchReplay.cs:86-111`). That works, but each Playwright release repeats it. The owner: "anything that
improves the experience for repository-owners can be applied".

The pull-request check did its job: nothing reached master. This PRD is about the bot moving such pins itself.

## 2. Findings (evidence)

Paths under `apps/CodeCoverage/`; `da/` = `dependency-action/src`, `cc/` = `CodeCoverage/Dependencies`.

- **F1 Discovery is Renovate plus our own SDK scanner.** Renovate `44.125.2` runs `--platform=local
  --dry-run=lookup` (`da/lookup/renovate.ts:10-11,92-99`) with every default manager (no `enabledManagers`).
  `renovateConfig()` (`renovate.ts:28-51`) adds presets and the `ignoreUnstable` packageRules, and **merges the
  repository's own `renovate.json`** (`:35`). Our scanner `da/platform/locations.ts` only finds SDK locations, only on an
  SDK move.
- **F2 No custom locations.** `customManagers`, `regexManagers`, `matchStrings` and `# renovate:` appear nowhere.
  `workflow-value` (`locations.ts:489-501`) is the SDK's `setup-*` values only; `platforms.<sdk>.locations` takes path
  globs for SDK locations only (`cc/DependenciesYml.cs:169-184`).
- **F3 Groups span ecosystems, but each member moves to its own version.** Group = configured, else Renovate's
  `groupName`, else the name (`cc/Policy/DependencyPolicyEngine.cs:90-95`); a rule's `ecosystem` is optional
  (`DependencyPolicyConfig.cs:88-90`). Nothing makes members land on the *same* version.
- **F4 Companions don't fit.** `DependencyCompanions.cs:18-35` holds fixed glob lists per SDK that wait for an SDK
  major. They can't say "this value follows that dependency".
- **F5 No own-image concept.** A new Docker tag is checked against its registry before it's written
  (`dependency-updates.md:256`), so an unbuilt own tag would be reported as not moved (scope of that check unconfirmed:
  S4).
- **F6 Renovate's own answers** (docs, research 2026-11-15):
  - it never reads `npm install x@v` in a `RUN` line (the npm manager reads package files; the dockerfile manager's
    `RUN` support is `apk`/`apt` only);
  - it **expands Dockerfile `ARG`s in `FROM`** and writes the update to the `ARG` line
    (https://docs.renovatebot.com/modules/manager/dockerfile/);
  - `customManagers` of `customType: regex` take `matchStrings` with `currentValue` and `depNameTemplate`/
    `datasourceTemplate`/`extractVersionTemplate` (https://docs.renovatebot.com/modules/manager/regex/), so a
    location can report as `mcr.microsoft.com/playwright` with `^v(?<version>[0-9.]+)-noble$`;
  - `groupName` groups across managers; it doesn't force equal versions either.

## 3. Goals

- G1 A version written somewhere the bot can't parse moves with the dependency it follows, in the same pull request.
- G2 A tag of an image the repository builds itself moves with the base image it's built from.
- G3 Pins that must be equal land on one version or not at all (no half-moves like #669).
- G4 What isn't moved is said in the pull request body, with the line to add, never a silent red build.
- G5 Zero configuration where the bot can infer it; one obvious `coverage.yml` line where it can't.

Non-goals: building or pushing images; editing the repository's own test scripts.

## 4. Options

| # | Option | For | Against |
|---|---|---|---|
| A | **`dependencies.follow:`** in `coverage.yml`: `{ path: glob, match: regex with (?<version>…), dependency: <name>, transform?: regex }`. The bot writes the dependency's new version (after the transform) wherever the match is, in the same group. | Bot-native, works without Renovate knowledge, fits G4 (the bot knows what it should have moved), same config file as everything else. | A new key and its parser/validator; regex in yaml. |
| B | **Honour Renovate `customManagers`** from the repository's `renovate.json` (already merged, F1): map `regex`-manager deps in `mapReport` and write them in the apply step. | Standard Renovate config; owners coming from Renovate already know it. | Needs S1/S2; a `customManagers` dep is its own dependency with its own lookup, so equality (G3) still needs grouping; two config files. |
| C | **Recommend `ARG`** in Dockerfiles: `ARG PLAYWRIGHT=1.64.1`, `FROM …:v${PLAYWRIGHT}-noble`, `RUN … playwright-core@${PLAYWRIGHT}`. One literal, Renovate moves it (F6). | No code if S3 holds; also removes the duplication. | Covers (a) only, never the compose tag or the NuGet package; needs S3 (prefix/suffix around the variable, and our applier writing the ARG line). |
| D | **Own images inferred:** an image `image: <registry>/<owner>/<name>:<tag>` whose name is pushed by a workflow in this repository from an in-repo Dockerfile, with the tag computed from that Dockerfile's `FROM` (as `seotester-deploy.yml:155` does), follows that `FROM`. | Zero config (G5) for the common "own image tagged with its base version" pattern. | Inference over workflow scripts is fragile; S5 decides if it is tractable, else D falls back to A. |
| E | **Lockstep groups:** `rules[].lockstep: true` (or implied by `follow:`): the group moves only to a version every member has (Microsoft.Playwright and the docker image both at 1.64.1), else it waits with a Reason. | G3; Playwright ships NuGet and the image at different times. | A new hold reason; versions across ecosystems compared by normalized semver (S6). |

**Recommendation (after S1-S7):** A + E, and B, which S1 + S2 show is already written by the apply step. ~~C documented
as the simplest owner-side fix~~ (superseded by S3: the applier cannot write an `ARG`-expanded tag). D is dropped (S5).

For SeoTester that is:

```yaml
dependencies:
  rules:
    - match: ["Microsoft.Playwright", "mcr.microsoft.com/playwright"]
      group: playwright
      lockstep: true
  follow:
    - dependency: mcr.microsoft.com/playwright
      path: apps/SeoTester/chromium/Dockerfile
      match: 'playwright-core@(?<version>[0-9.]+)'
    - dependency: mcr.microsoft.com/playwright
      path: apps/SeoTester/docker-compose.yml
      match: 'seotester-chromium:(?<version>[0-9.]+)'
```

(The image's new tag `v1.64.1-noble` gives `1.64.1`: a follower takes the first `x.y.z[-pre]` of its leader's new
value unless it sets `version:`; S7.)

## 5. Spikes

| # | Question | How | Decides |
|---|---|---|---|
| S1 | Does a `renovate.json` `customManagers` regex dep appear in our lookup report, and does `mapReport` drop or keep it? | Fixture repo with the SeoTester Dockerfile + a customManager; run the pinned Renovate image in `--dry-run=lookup`; read `mapReport.ts`. | B |
| S2 | Can the apply step write a location it didn't extract itself (a report's `packageFile` + `replaceString`)? | Read the apply path; one test. | B |
| S3 | Does Renovate 44.125.2 resolve `FROM img:v${X}-noble` with `ARG X=…` (prefix/suffix), and does our applier write the `ARG` line? | Fixture + dry-run; read the applier. | C |
| S4 | Is a new Docker tag checked against its registry outside SDK moves, and what does the body say for an own image? | Read `locations.ts`/apply; Spark run 43715251776's body. | D, G4 |
| S5 | Can "own image tagged with its `FROM` version" be inferred from workflows? Count the patterns in Spark's 4 image workflows and 2 onboarded repos. | Read workflows. | D |
| S6 | Do NuGet `Microsoft.Playwright`, npm `playwright-core` and `mcr.microsoft.com/playwright` publish the same versions, and how far apart in time? | Registry histories for the last 10 releases. | E (how often lockstep waits) |
| S7 | Can a dependency's new version be expressed without its tag decoration (`v1.64.1-noble` → `1.64.1`) using the existing docker versioning, or does `follow:` need a `transform`? | Read the docker versioning in the engine. | A's shape |

## 6. Owner questions (after the spikes)

- Q1 A (`coverage.yml follow:`) vs B (Renovate `customManagers`) vs both.
- Q2 Lockstep: an explicit `lockstep: true`, or implied for every group a `follow:` names?
- ~~Q3 D (inference) if S5 finds a reliable rule, or keep own images explicit through `follow:`.~~ Answered by S5:
  explicit.
- Q4 D1 (the Applied table per group) and D2 (tag format change) in this pull request?

## 7. Plan

- [x] **M0 Spikes S1-S7**, results in §7.1 with evidence; owner answers Q1-Q4 (H1-H4). S1/S3 are pinned by
  `dependency-action/src/apply/adapters/customManagers.test.ts` (6 tests, green on today's code) on the real report
  `test/fixtures/renovate/custom-managers` (SOURCE.md).
- [x] **M1 `follow:` parsing** (`DependenciesYml.cs`, `DependencyFollowRule`, `rules[].lockstep`): red 14 → green 60/60
  (`DependenciesYmlTests`, 2026-11-15). Validation: exactly one `version` group (declarations counted, since .NET
  merges same-named groups), compiles, matched with a 250 ms timeout (a Fibonacci-backtracking pattern throws
  `RegexMatchTimeoutException`; `(a+)+` does not, the engine makes it atomic), path a repository-relative glob.
  Whether the dependency is in the plan is checked at plan time (M2), where an unknown one becomes a "not moved" line.
  The default follower version is the first `x.y[.z]`, with a prerelease only when it starts with a prerelease word
  (`v1.64.1-noble` → `1.64.1`, `11.0.0-rc.2-noble` → `11.0.0-rc.2`, `22.11.0-alpine3.20` → `22.11.0`).
  Doc + setup-page snippet: M7.
- [x] **M2 Writing followers** in the apply step, inside the leader's group; the body lists each follower moved, and a
  follower whose match found nothing as "not moved" with its rule (G4). Done 2026-11-15 per §7.1a:
  - Action: `lookup/follow.ts` (+ `follow.test.ts`, 17 tests), `lookup.test.ts` on the real S1 fixture, manager
    `follow` in `replaceString.ts`. Server: rules in the platform table (`DependencyPlatformTable.Json`,
    `PlatformLifecycleTests.The_table_carries_the_follow_rules…`).
  - Writer (F3): `DependencyFollowRule` moved to `CodeCoverage.Git/Dependencies` so the writer can run it; the request
    carries `Follow` (wire `FollowRuleWire`, capped at 100 rules in `DependencyFields.Follow`, mirrored by the parser).
    `DependencyFollowPatchTests` on SeoTester's real Dockerfile and compose file: red 5/10 → green, 21 tests with the
    wire and F4 cases.
  - **Found on the way:** today's `SubstitutionCheck` already admitted the compose `image:` line, because the comment
    two lines above says "Playwright" (fixture lines 154-156); that's F1 by accident. Since F3, a `follow` record passes
    only through its rule (`Without_its_rule_a_follow_record_is_refused_even_on_a_line_naming_the_dependency`).
  - Grouping: a follower takes its leader's `depName`, so the name-keyed group holds both (`FollowGroupingTests`).
  - Body: the server never read `lookup.problems`; `follow[i]` problems now reach "Not moved"
    (`DependencyPolicyOutcome.NotFollowed`, `A_follow_rule_that_moved_nothing_is_listed_under_not_moved`).
- [x] **M3 Lockstep groups** (E): the planner picks the highest version every member has; otherwise the group waits with
  Reason "lockstep: `<member>` has no `<version>` yet". `LockstepTests` (red 3/6 → green): #669's half-move reproduced
  without lockstep; waiting; the highest common version; catch-up. Members are compared by their first `x.y.z[-pre]`
  (`DependencyFollowRule.DefaultVersion`), ordered by `SemanticVersion` (now public). A member with no update counts at its
  current version. Before, such records were dropped before grouping, so they are now kept aside by group and join only
  a lockstep group.
- [x] **M4 Body:** D1 (Applied per group), D2 (tag format change named), per Q4. D1: one row per dependency and move, with
  "N files" when there are several, and every group's first row before any second row
  (`The_applied_table_names_every_group_and_collapses_one_move_across_files`). D2: a value whose text before or after the
  version changes is named under the table (`A_tag_whose_format_changes_is_named`). D2's upstream question (is
  `boky/postfix:4.4.0` the intended tag?) is left for the owner's review of #669.
- [x] **M5 Renovate `customManagers`** (B): no new code beyond manager `regex` in `replaceString.ts` (S1). Pinned by
  `customManagers.test.ts` (action) and the two F4 cases in `DependencyFollowPatchTests` (writer).
- [x] **M6 Spark's own `coverage.yml`**: group `playwright` (Microsoft.Playwright + the image), four `follow:` rules (the
  `playwright-core@` install, our image tag, and the two comments naming the version). Guarded by
  `DependenciesYmlTests.Sparks_own_follow_rules_find_every_seotester_pin_at_the_package_version`. The next Playwright
  release must arrive as one green group (to observe after deploy).
- [x] **M7 Docs:** `dependency-updates.md` §4, "Pins that follow a dependency" (`follow:`, lockstep, customManagers, the
  `ARG` advice).
- [ ] **M8 Gate:** PR, CI green, coverage via the MCP.

### 7.1 Spike results (2026-11-15)

- **S2 (done): the apply step already writes a location it didn't extract.** The `replace-string` adapter matches any
  dependency with a `replaceString` whose *datasource* is `docker` (or a few others), whatever the manager
  (`dependency-action/src/apply/adapters/replaceString.ts:14,71`). `mapReport` keeps every manager's deps and derives
  the ecosystem from the datasource (`lookup/mapReport.ts:151-178`, `ecosystemOf` `:103-115`). A Renovate regex-manager
  dep reported as `mcr.microsoft.com/playwright` (datasource `docker`) would therefore be planned and written with **no
  new code**. To confirm with a real lookup report: S1.
- **S4 (done): the registry check is SDK-only.** `tagExists` is used only in `platform/applyPlatform.ts:232,288,329`
  (P7). A package-level docker update writes the tag Renovate looked up. An own-image follower is never looked up and
  must not be: its tag exists only after the repository's deploy builds it (`seotester-deploy.yml:155,176` builds
  before compose pulls).
- **S5 (done): own images can't be inferred reliably.** Of Spark's image workflows, only `seotester-deploy.yml:155`
  derives a tag from a `FROM` line, through an ad-hoc `sed` script; the others tag through `docker/metadata-action`
  (`code-coverage-deploy.yml:135,205,274`, `mintplayer-deploy.yml:105`, `seotester-deploy.yml:111,238`). Inference
  would mean parsing shell. **D is dropped; own images are declared through `follow:` (or a regex custom manager).**
- **S6 (done): the three registries don't publish the same versions.** Last 10 releases:
  - NuGet `Microsoft.Playwright`: … 1.62.0, 1.63.0, 1.64.0, 1.64.1. No 1.61.1, 1.62.1, and **no 1.65.x** yet.
  - npm `playwright-core`: … 1.64.1 (2026-10-14), 1.65.0 (2026-11-05), 1.65.1 (2026-11-13).
  - MCR `playwright`: `v1.58.0-noble` … `v1.65.1-noble`, every npm version.

  So without lockstep (E) the image can move to 1.65.1 while NuGet stays at 1.64.1, which `ConnectAsync` refuses.
  #669 landed both on 1.64.1 by coincidence. **E is needed**, and a lockstep group waits often (NuGet lags by weeks
  and skips patches).
- **S7 (by design): the follower's value is the first `x.y.z[-pre]` in the leader's new value** (`v1.64.1-noble` →
  `1.64.1`), overridable with `version:` (a regex with a `version` group, applied to the leader's new value), the same
  role as Renovate's `extractVersionTemplate`.
- **S1 (done): a `customManagers` regex dependency reaches the report the way the apply step needs it.** The pinned
  image (`renovate/renovate:44.125.2@sha256:a7dec…`, the bot's own config from `renovateConfig()`, `--dry-run=lookup`)
  on a fixture (SeoTester's Dockerfile and compose line at 1.63.0, two regex managers with `depNameTemplate:
  mcr.microsoft.com/playwright`, `datasourceTemplate: docker`, `extractVersionTemplate: ^v(?<version>[0-9.]+)-noble$`)
  reports, under manager `regex`:
  - `chromium/Dockerfile` `mcr.microsoft.com/playwright` `docker` `currentValue 1.63.0` `replaceString
    "playwright-core@1.63.0"`, updates `1.64.1`, `1.65.1` (minor);
  - `docker-compose.yml` the same, `replaceString "seotester-chromium:1.63.0"`.

  The `extractVersionTemplate` strips the tag decoration, so `newValue` is `1.64.1`, not `v1.64.1-noble`. With S2,
  B needs **no new code in the action** (datasource `docker` + `replaceString` → the `replace-string` adapter). What B
  still needs is the planner putting those deps in the leader's group (they share its name, so the name-keyed group
  does that) and lockstep (they move to npm's versions, which NuGet lags; S6).
- **S3 (done): Renovate resolves the `ARG`, but nobody can write it.** For `ARG PLAYWRIGHT=1.63.0` + `FROM
  mcr.microsoft.com/playwright:v${PLAYWRIGHT}-noble` the report has `currentValue v1.63.0-noble` and `replaceString
  "mcr.microsoft.com/playwright:v1.63.0-noble"`: the *expanded* text, which is not in the file. The `replace-string`
  adapter counts occurrences of `replaceString` (`apply/adapters/replaceString.ts:40,90`) and refuses at 0x. **C is
  not recommended**: an `ARG` pin would arrive as a refused group every time. (Supporting it means rewriting the
  `ARG` default instead; out of scope, not asked for.)

### 7.1a Design of M2 (2026-11-15)

- **F1 S1's "no new code" holds for the action, not for the server.** The server's `SubstitutionCheck` accepts a
  changed line only when it (or the 3 lines above) names the dependency (`CodeCoverage.GitWriter/Jobs/Dependencies/
  SubstitutionCheck.cs:37-48`, `Anchors` `:110-131`). `image: ghcr.io/mintplayer/seotester-chromium:1.64.1` names
  neither `mcr.microsoft.com/playwright` nor `playwright`, so the compose follower would be refused, whether it came
  from `follow:` or from a `customManagers` regex.
- **F2 A follower is a lookup record the action finds itself**, like a platform location (`lookup/lookup.ts:73-75`):
  the bot passes the `follow:` rules in the lookup's `platforms` table (`DependencyPlatformTable.Json`, new key
  `follow`); after `mapReport` the action globs each rule's `path`, runs its `match`, and emits one record per match:
  manager `follow`, the leader's `depName` and `datasource`, `currentValue` = the `version` group, `replaceString` =
  the whole match, candidates = the leader's candidates with each value mapped to the follower's version (S7; a
  candidate that maps to nothing is dropped). So the follower is in the leader's group by name, moves with it, and
  waits with it; no new plan field, and the `replace-string` adapter writes it (manager `follow` joins its managers).
  A rule whose dependency the lookup didn't report, or whose pattern matched nothing, is a lookup *problem*, shown in
  the body as "not moved" (G4).
  - Not through Renovate `customManagers`: the config would need the leader's datasource before the lookup has run.
- **F3 The validator trusts the rule, not the report.** The patch request carries the `follow:` rules (from the
  default branch's `coverage.yml`, like every other setting). A changed line in a file matching a rule's `path` passes
  when the old line matches the rule's `match` with `version` = the entry's current value and the new line is the
  same with the new value. The rule widens the allow-list to its own paths; the deny-list still wins (`.github/`,
  `coverage.yml`, `.mintplayer/`). A report never widens it (§13.5 P5).
- **F4 B (`customManagers`) stays as Renovate reports it:** its records keep manager `regex`, and the
  `replace-string` adapter writes them (manager `regex` joins its managers). The validator applies the usual rules:
  the file must be a manifest of the datasource (a Dockerfile or compose file for `docker`), and the line must name
  the dependency. So SeoTester's `playwright-core@` line passes as B, and its compose tag needs `follow:` (A).
- **F5 Pattern dialect.** The server checks a pattern with .NET and the action runs it with JavaScript. A pattern
  JavaScript cannot compile is a lookup problem naming the rule. The documented subset is: named group
  `(?<version>…)`, character classes, quantifiers, anchors.

### 7.2 Found on the way (#669's body)

- **D1 The Applied table hides groups.** #669's *Applied* table stops at 60 rows ("… and 18 more"), and the
  Playwright group was among the hidden ones: the body never named the group that broke the build. Fix: one row per
  **group** (members collapsed, "37 files"), or every group listed before any per-file row is cut.
- **D2 A tag format change passes as a minor.** `boky/postfix` `v4.3.0` → `4.4.0` (Applied) and `v4.3.0` → `5.2.1`
  (Awaiting approval) dropped the `v` prefix. To check: whether `4.4.0` exists as that image's tag and whether the
  change is intended upstream; at least the body should say the tag's format changed.

## 8. Test plan

- Real fixtures: SeoTester's Dockerfile and compose file, a real Renovate lookup report for S1/S3.
- `follow:` parser: valid, no `version` group, two groups, catastrophic regex (timeout), unknown dependency.
- Apply: follower moved; follower not found → body line; follower in a held group stays.
- Lockstep: all members have the version → moves; one lacks it → waits with Reason; the version every member has is
  lower than each member's newest → moves to the common one.

## 9. Decision log

| # | Decision / evidence | Source |
|---|---|---|
| E1 | #669's red is the half-move; the check caught it at pull-request time. | run 43715251776, job 132815264771 |
| E2 | A person's commit on the rolling branch survives rebuilds. | `RollingBranchReplay.cs:86-111` |
| E3 | Scope: anything that improves repository owners' experience may be applied. | owner, 2026-11-15 |
| H1 | Q1 → both: `dependencies.follow:` in `coverage.yml` **and** Renovate `customManagers` from `renovate.json`. | owner, 2026-11-15 |
| H2 | Q2 → lockstep is implied for every group a `follow:` names; any other group opts in with `lockstep: true`. | owner, 2026-11-15 |
| H3 | Q4 → D1 (Applied per group) and D2 (tag format change named) in this pull request. | owner, 2026-11-15 |
| H4 | S1/S3 may run the pinned Renovate container locally on a fixture. | owner, 2026-11-15 |
| I1 | `DependencyFollowRule` lives in `CodeCoverage.Git/Dependencies`, so the GitWriter runs the same compiled rule the parser validated (F3). | `DependencyFollowPatchTests` |
| I2 | A `follow` entry passes only through its rule, never the manifest/anchor path; a regex timeout refuses it as an unplanned change. | `Without_its_rule_a_follow_record_is_refused_even_on_a_line_naming_the_dependency`, the timeout test |
| I3 | The wire caps `follow` at 100 rules (512 KiB of the body), no control characters, a relative glob without `..`; the parser applies the same caps. | `DependencyFields.Follow`, wire tests in `DependencyFollowPatchTests` |
| I4 | Lockstep compares members by their first `x.y.z[-pre]` with `SemanticVersion` (made public); a member without candidates counts at its current version, so such records are kept aside by group instead of dropped. | `LockstepTests` (red 3/6 → green) |
| I5 | D1 collapses one (dependency, from, to) across files and lists every group's first row before any second row, under the existing 60-row cap. D2 notes a change in the text around the version; prerelease → release is not a format change. | `DependencyPullRequestBodyTests` |
| I6 | Only `follow[i]` lookup problems reach the body ("Not moved"); the server still ignores the other problem sources, as before. | `A_follow_rule_that_moved_nothing_is_listed_under_not_moved` |

### 9.1 Files changed

- Action: `lookup/follow.ts` (+ test), `lookup/lookup.ts`, `contracts/types.ts`, `apply/adapters/replaceString.ts`,
  `platform/*`, `util/repoFiles.ts`, `customManagers.test.ts` + fixture, `dist/index.js`.
- Server: `DependenciesYml.cs`, `Policy/DependencyPlatformTable.cs`, `Policy/DependencyPolicyConfig.cs`,
  `Policy/DependencyPolicyEngine.cs`, `Policy/DependencyPolicyModels.cs`, `Lifecycle/DependencyLifecycleRules.cs`,
  `Lifecycle/DependencyPullRequestBody.cs`, `Lifecycle/DependencyUpdateEngine.cs`, `MergeQueue/GitWriterSidecarClient.cs`.
- Shared: `CodeCoverage.Git/Dependencies/DependencyFollowRule.cs` (new), `Manifests/SemanticVersion.cs` (public).
- GitWriter: `DependencyPatchValidator.cs`, `DependencyPatchContracts.cs`, `DependencyJobContracts.cs`,
  `Endpoints/DependencyEndpoints.cs`, `Jobs/WriterService.Dependencies.cs`.
- Repo: `coverage.yml` (playwright group + 4 follow rules), `docs/code-coverage/dependency-updates.md`.
