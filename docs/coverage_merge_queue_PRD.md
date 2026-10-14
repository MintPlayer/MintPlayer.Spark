# PRD — a conflict-resolving merge queue in CodeCoverage (rebase → auto-resolve → `postRebaseActionName` → squash)

**Status:** Grilled 2026-10-14; the owner decisions are O1–O17 in §0, and they supersede §§1–8 where they disagree. Spikes S1–S8 have not been run (S9 moved to #422 under O7). ~~No owner grill yet~~
**Date:** 2026-10-14 · **Issue:** none yet · **App:** `apps/CodeCoverage` (production, coverage.mintplayer.com)
**Origin:** a four-agent investigation, 2026-10-14:
- the CodeCoverage codebase (forge abstraction, webhooks, git sidecar, configuration, deployment)
- Spark's generated files (`modelHashes.json` and synchronize)
- the forge APIs (GitHub, GitLab, Bitbucket) and prior-art merge queues
- git mechanics and hardening for rebasing untrusted repositories

Claims about this repository carry a `file:line` pointer (paths relative to `apps/CodeCoverage/` unless stated).
- **[verified]** means read in the source.
- **[cited]** means taken from the linked external page.
- **[inferred]** means reasoned, not measured.

Every inferred claim that a decision rests on has a spike in §7.

## 0. Owner decisions (2026-10-14)

These supersede the text below where they disagree. Superseded passages are marked **[superseded by On]**.

| # | Decision | Evidence / reason |
|---|---|---|
| O1 | **Only command-generated files are auto-resolved.** A conflict in any other file, including `translations.json`, `security.json` and `programUnits.json`, **evicts** the PR. The `json-union` strategy is dropped. | Owner. Hand-authored content is never merged by the bot. |
| O2 | **A conflict is resolvable only if every conflicting hunk falls on an allow-listed hash line.** For `modelHashes.json` those are the roll-ups `modelHash`, `contextRoots` and `modelFiles`. A conflict on any other line evicts. | Owner. Note: `Model/<Entity>.json` holds **no** hash line (`apps/DemoApp/DemoApp/App_Data/Model/Address.json` has no `hash` key, checked 2026-10-14). The hash lines all live in `App_Data/modelHashes.json`, so a conflict inside a model file always evicts. |
| O4 | **The translations seeder inserts new keys in alphabetical (ordinal) position instead of appending.** Part of M1. All apps' `translations.json` are re-sorted once in the same PR. | Owner, grill 1. Appending (`TranslationsSeeder.cs:103-140`) makes two model PRs conflict at the end of the file, which O1 would evict. Sorted inserts let git merge them cleanly. |
| O5 | **`--spark-synchronize-security` rewrites `security.json` with `rights[]` sorted.** The order is the resource's target (`Person` in `QueryReadEditNewDelete/Person`), then `groupId`, then the right part (`QueryReadEditNewDelete`), then `key` as the final tie-break. The rewrite must keep the one-right-per-line layout, `_comment`, `$schema` and key order, so a no-op run produces a byte-identical file. `--spark-verify-security` exits 3 on an unsorted array. Every app's file is re-sorted once in the same PR. | Owner, grill 2 (option B). Today an appended right conflicts with every other model PR's appended right. |
| O6 | **[superseded, grill 4]** The owner kept R3 of `docs/model_sync_lifecycle_PRD.md:90-130`: the synchronize flags work in every environment, because the explicit flag is the intent. `spark-regenerate` runs as Production, like the existing verify step. Original proposal, struck: ~~**Both synchronize commands run only when `ASPNETCORE_ENVIRONMENT=Development`.** In any other environment the flag is ignored and the web app starts normally. The verify commands stay environment-independent, because CI runs them. The `spark-regenerate` action sets `Development`.~~ | Owner, grill 2. Today `SynchronizeSparkModelsIfRequested` has no environment check (`libs/spark/MintPlayer.Spark/Extensions/SparkDevelopmentExtensions.cs:80-101`, checked 2026-10-14). |
| O7 | **GitHub only in this PR.** `IForgeMergeQueue` stays forge-neutral, with conformance tests. GitLab and Bitbucket land in a separate PR under #422, together with those integrations. M8 and S9 are dropped from this PR. D10 and Q1 are **[superseded by O7]**. | Owner, grill 3. The research notes are posted on #422. |
| O8 | **Three ways to enqueue:** a `merge-queue` label, a `/queue` (and `/dequeue`) comment, and a button on the CodeCoverage repository page. The button resolves the CodeCoverage user to their linked GitHub login (the `provider:login` owner keys already used by `GitHubProjectVisibility.cs:63-86`). A user with no linked GitHub identity is refused, with a message. Every path then runs the same permission check (Q2). Q3 is **[superseded by O8]**. | Owner, grill 5. |
| O9 | **Who may enqueue:** a GitHub login with at least `write` *role* on the repository (`GET /repos/{o}/{r}/collaborators/{login}/permission`). It is re-checked right before the merge, and the PR is evicted if the role is gone. Q2 is **[superseded by O9]**. **To force every merge through the queue**, the repository uses two rulesets (Trunk's documented pattern, §4.1). Ruleset 1, "Restrict updates" on the base branch, has only the App as an *Exempt* bypass actor, so *Squash and merge* is blocked for everyone, maintainers and admins included. Ruleset 2 holds the required reviews and required checks and has **no** bypass actors, so the bot is held to them too. The role check above is independent of ruleset 1, so maintainers keep `write` and can still enqueue. | Owner, grill 6. A single ruleset with the App as bypass actor would let the bot skip the reviews and checks as well. That is why the two are split. S5 verifies this. |
| O10 | **Security requirements: the bot merges someone else's code, so it must never merge more than what was approved or authorised.** See §5.7. | Owner, grill 6. |
| O11 | **The approval floor is a setting on the organization (Account) page, with a per-repository override**, following the existing `DeleteBranchOnPrClose` org-default + repo-override pattern (`Account`/`Repository` entities, §3.5). Values: `RepositoryRules` (no floor of our own), `ExternalAuthors` (a PR by an author without `write` needs one `APPROVED` review by a `write` user on the pinned SHA, checked at enqueue), `Always` (at least one approval from someone other than the author). Default `ExternalAuthors`. MintPlayer, which has a single member, can choose `RepositoryRules`. Only the org's owner, through the existing live forge ownership check, may change it. | Owner, grill 7. These are policy settings, not repo content, so they live in the DB and not in `coverage.yml`: a PR can't touch them. |
| O12 | **The queue waits for every check run and commit status on the final commit**, minus an **exclusion list** set by the org owner on the organization page, with a per-repository override (as in O11). Ruleset-required checks are always included and cannot be excluded. The queue only moves on once every `check_suite` for the final SHA has completed **and** every check in an expected-minimum set has reported. That set defaults to the checks that reported on the base branch's latest commit. Exclusions are listed on every PR's queue check run. | Owner, grill 8. `MintPlayer/MintPlayer.Spark` `master` has no ruleset and no branch protection (`gh api …/rules/branches/master` → `[]`, `…/branches/master/protection` → 404, 2026-10-14), so "required checks only" would leave the queue with no gate at all. GitGuardian's false positives on `modelHashes.json` (e.g. the `"ApiToken": …` hash line) are the motivating exclusion. |
| O13 | **An "Optimal GitHub setup" page in the CodeCoverage Angular app.** It covers the two rulesets (O9), the native merge queue off (D8), "dismiss stale approvals" and "require approval of the most recent push" (T3), the regenerate workflow template (§5.6) with its no-secrets rule (T5), and accepting the new App permissions. Screenshots are taken with the `playwright_node` MCP. Before each capture, a `browser_evaluate` blanks out identifying content: account names, avatars, emails, repo names other than a neutral demo repo. Images use native `loading="lazy"` with explicit `width`/`height`. ng-bootstrap's `[bsInViewport]` (`@mintplayer/ng-bootstrap/viewport`, `BsInViewportDirective`) is used only if something else needs to react when an element enters the viewport. | Owner, grill 8. The screenshots show GitHub's settings pages, so the MCP browser needs a signed-in GitHub session; the owner signs in in that browser, since credentials never pass through the assistant. Blanking out personal data follows the no-personal-data-in-fixtures rule. |
| O14 | **Q4 and Q5 are answered from the evidence. Signed commits:** `master` requires none (no protection, see O12), so v1 publishes with a git push. The Git Data API publish path is built only if S5 shows an Unverified bot head blocks a squash. **Permissions:** the owner is the only member of MintPlayer and accepts the raised App permissions there. | 2026-10-14, checked against the API. |
| O15 | **Configuration is split.** Trust policy is in the UI (DB): the approval floor (O11) and check exclusions (O12). File behaviour is in `coverage.yml` `mergeQueue:`: `postRebaseActionName`, `autoResolve`, `regeneratedPaths`, `branches`, the enqueue label and timeouts. The queue check run lists the **effective** configuration, each value labelled with its source. | Owner, grill 9. File paths and workflow names change with the code; trust decisions belong to the owner. |
| O16 | **`coverage.yml` `mergeQueue:` is read from the repository's *default* branch, never from the PR's base branch.** Wherever this document says "read from the base ref" for the merge queue (§5.2, D3, T4, T6), that is **[superseded by O16]**. A PR whose base is not listed in the default branch's `branches:` cannot be enqueued. | Owner, grill 9. Otherwise someone could change the config on branch A, then open a PR from B into A, and the queue would run under A's config, for example with a widened `autoResolve`. The existing coverage-feedback precedent (`PublishFeedbackRecipient.cs:65-69`) reads from base; that is unchanged here. |
| O17 | **"Accept the new permissions" link.** It is resolved at runtime from the installation's own `html_url` (`GET /app/installations/{installation_id}` with the App JWT, through `IGitHubInstallationService.CreateAppClientAsync`). It is never hard-coded and never built from the account name. The link appears on the organization page, on the setup-guide page (O13), and on the queue check run when a call fails for lack of `actions`/`workflows` permission. The installation's `permissions` are compared against the set the queue needs, so the page can say exactly which ones are still pending acceptance. | Owner, 2026-10-14. The App id and private key are already configured, and `Account.InstallationId` is stored (§3.1). **Implemented (organization page):** `GitHubInstallationPermissions` (CodeCoverage.GithubIntegration/Services) behind the neutral `IForgeInstallationPermissions` (CodeCoverage.Library/Forge), served by `GET api/accounts/{provider}/{login}/pending-permissions` (`AccountPermissionsController`, `Manage/RepoSettings` + owner check, 404 for non-managers), rendered by `app-account-permissions-notice` on the Account page. **Deviation:** pending is computed against everything `GET /app` declares, not only the queue's set — any raised permission blocks something, and a hard-coded "queue set" would drift. Compared over raw JSON (`IConnection.GetRaw`): Octokit 14's `InstallationPermissions` types 36 permissions and misses e.g. `merge_queues`. Cached 1 min per installation, failures included. Tests: `GitHubInstallationPermissionsTests` (15, green), `account-permissions-notice.component.spec.ts` (4) + `po-detail-page.component.spec.ts`, green. Docs: `docs/code-coverage/hosting.md` under **Permissions**. Still open: the setup-guide page (O13) and the queue check-run link reuse `IForgeInstallationPermissions`. |
| D12 | **T5(a) refuted live, and its mitigation strengthened.** A dispatched run uses the workflow file **from the dispatched `mq/` ref**, not from the default branch. The default branch only *registers* the workflow: one that exists only on `mq/` gives 404; one deleted on `mq/` gives 422. So the queue (1) evicts any PR whose net change touches `.github/**`, and (2) before dispatching, asserts that the `postRebaseActionName` blob on the `mq/` commit equals the default branch's blob. "`contents: read`, no secrets" (T5b) stays the main defence, because the PR's build code runs either way. | Scratch repo `PieterjanDeClippel/mq-spike-scratch`, 2026-10-14: a marker edited only on `mq/` shows `MQ-VERSION` in the run log and the artifact. Evidence in the scratchpad file `spikes_live.md`. |
| D13 | **Merging uses `PUT /pulls/{n}/merge-async`** (apiVersion 2026-03-10) with `merge_method=squash`, `sha` and **`merge_action=direct_merge`**. With `default`, a repository that has the native queue on would enqueue the PR there instead. Responses: a stale `sha` gives **400** ("head branch was modified"; §5.1's "409" is corrected). 202 means pending. A repeat returns 200 `merged` with the merge commit SHA, so a retry is safe. 409 means a merge request is already pending. | Live calls on the scratch PR #1, 2026-10-14. |
| D14 | **Correlating the dispatch:** `return_run_details=true` returns 200 with `workflow_run_id`. The run builds the branch head **as of dispatch time**, even if a commit is pushed 2 s later. The queue compares the run's `head_sha` (`GET /actions/runs/{id}`) with the `preparedSha` and ignores any mismatching run. Pushes to `mq/` fire `push` workflows only, never `pull_request` (`pull_requests: []`), so the setup guide tells consumers to add `branches-ignore: ['mq/**']` to `push:` triggers. | Live, 2026-10-14. |
| D15 | **Rulesets API 403 "Upgrade to GitHub Pro…"** (a private repo on the Free plan) is treated as **"no rules"**, not as a permission error. Whether `metadata: read` can read `rules/branches/{b}` is still open (S5). | Live, 2026-10-14. |
| D16 | **T7 confirmed live with the App token.**
- An App that is `exempt` from ruleset 1 is still held to ruleset 2, even with `bypass_rules: true`.
- A direct push to main is rejected (GH013), and so is a `merge-async` while reviews or checks are unmet.
- `GET /rulesets/{id}` exposes `current_user_can_bypass` (`exempt`/`never`). The enable-time check reads it and refuses the setup when ruleset 2 shows anything other than `never`.
- `rules/branches/{b}` is readable with **`metadata=read`** (`X-Accepted-GitHub-Permissions`), which answers D15's open point. | Dev App on `PieterjanDeClippel/mq-spike-public`, rulesets 25066850 / 25066861, 2026-10-14. Evidence in the scratchpad file `spikes_app.md`. |
| D17 | **A `merge-async` 202 is not a merge.** The PUT always answers 202 "enqueued"; the rules are evaluated afterwards. The saga polls the undocumented `GET /pulls/{n}/merge-async/{uuid}` (and/or waits for `pull_request.closed` with `merged`). A `failed` status carries `details.message` (e.g. a rule violation), which is shown on the queue check run. A failed request does not stay pending: a retry gets a new uuid. A call made immediately after our own push can get 400 "head branch was modified" before GitHub registers that push. Retry once after a short delay. | Live, 2026-10-14. |
| D18 | **T3 corrected: GitHub dismisses approval only when the PR's *diff* changes.**
- A bot squash or rebase that keeps the same net diff keeps the approval, and `require_last_push_approval` does not block it.
- The **regenerate patch changes the diff**. Under `dismiss_stale_reviews_on_push`, the approval is then dismissed, and with last-push approval the merge requires a human re-approval ("New changes require approval from someone other than the last pusher").
- See open question Q6. | Live, scratch PRs #1–#4, 2026-10-14. |
| D19 | **App pushes fire both `push` and `pull_request` runs.** The App can push `.github/workflows/**` with `workflows: write`. The `workflow_run`, `check_suite`, `check_run`, `pull_request_review` (submitted and dismissed) webhooks all deliver. | Live, 2026-10-14 (`GET /app/hook/deliveries`). |
| D23 | **(M1, O4) Synchronize sorts the whole `translations.json` on every run, not only the keys it seeds.** Every object's members are put in ordinal key order, `$schema` pinned first, language members inside a translation included (all apps already had `en`, `fr`, `nl` in order: 0 unsorted leaves across the 7 files, checked 2026-10-14). The file is rewritten only when a seed is added or a key moved, so a sorted file in another layout is not reformatted. `--spark-verify-model` does **not** gate the order; the next synchronize fixes it. | A sort of the seeds alone leaves "sorted position" undefined among unsorted siblings, and a hand-appended key would sit at the end forever, conflicting exactly as before. Sorting everything makes order a fixed point, so two branches always agree on where a key goes. Tests: `TranslationsSeederOrderTests` (5; the 3 ordering tests red before: appended / not rewritten, green after). |
| D24 | **(M1+, O5) The rights reorder is a byte-range block move, not a line reorder, and accepts any layout.** `SecurityRightsOrder` (`libs/spark/MintPlayer.Spark/Services/SecurityRightsOrder.cs`) finds each `rights[]` element's byte range with `Utf8JsonReader` and moves the elements as opaque blocks; every byte outside them is kept. Already sorted: the input string is returned unchanged (byte-identical). Reordered: elements are joined by the separator the array uses most, so a blank line that grouped rights under the old order is dropped instead of landing between unrelated rights. There is no "one right per line" refusal: an unparsable file (e.g. conflict markers) exits **2** with nothing written. `--spark-verify-security` exits **3** on an unsorted array and still runs the posture comparison, so one run reports both. Synchronize sorts *before* the posture is described. | CodeCoverage's `security.json` has one right over **five lines** (`apps/CodeCoverage/CodeCoverage/App_Data/security.json:19-24` before the re-sort), so a line-based reorder would have refused the largest app. strict JSON (`SparkSecurityFiles.Compose`: "A layer is not strict JSON") allows only `,` and whitespace between elements, so a block move cannot strand a comment. Tests: `SecurityRightsOrderTests` (9; the 3 command tests red against the pre-change `SparkSecurityVerificationExtensions.cs`, green after). |
| D25 | **(S7) Fail closed is scoped to a model file that cannot be *parsed*.** `ModelSynchronizer.LoadExistingEntityTypeFiles` throws `ModelFileUnreadableException` (an `InvalidOperationException`) instead of printing, and `--spark-synchronize-model` maps it to exit 1 with the message on stderr. Load happens before any write (`ModelSynchronizer.cs:69`), so nothing is written. An `IOException` while *reading* a model file (`SparkModelFiles.ComposeLenient`, Abstractions) still prints and skips: that path is shared with the startup hash gate and is left as is. | The exit code was 0 before (red: `SynchronizeFailsClosedTests` "Expected exitCode to be 1, but found 0"). An unhandled exception would not give the documented 1 on Windows (0xE0434352), hence the explicit mapping. |
| O18 | **Q6 → B: the queue never pauses for re-approval.**
- The setup guide (O13) and the ruleset-2 template recommend `dismiss_stale_reviews_on_push: false` and `require_last_push_approval: false` on queue-managed branches.
- The queue covers the T3 gap itself: any push whose `after` is not one of the bot's recorded SHAs evicts the entry (§5.5). The bot's own changes are limited to `regeneratedPaths` and gated by the verify checks (T4).
- If a repository keeps those rules on anyway, the `merge-async` fails with a rule-violation message (D17). The entry is then **evicted**, and the check run explains which setting to change.
- The enable-time check (D16) **warns** when it reads either rule on ruleset 2. | Owner, grill 10. D18 showed that GitHub dismisses approval whenever the regenerate patch changes the diff; pausing would cost a human click on every model PR. T3's "dismiss stale approvals … should be on" is **[superseded by O18]** for queue-managed branches. |
| O19 | **Q7 → A: a conflict on any line of `modelHashes.json` (and `securityPosture.txt`) is resolvable with take-base.**
- O2's three-roll-up-line restriction is **[superseded by O19]** for these wholly generated files. The resolver's allow-list entry for them means "any line".
- Every other path still follows O1 (evict).
- Safety: a same-entity clash always conflicts in `Model/<Entity>.json`, which evicts (D6). The verify gate on the final commit catches a regenerate step that did nothing. | Owner, grill 11. The M2 tests showed two entities sorting into the same gap of `files`/`entities` conflict outside the roll-ups and were evicted under O2 (§D20–D22 notes). That would have evicted about half of all entity pairs. |
| O20 | **CI re-runs whenever the final tree differs from the tree CI last tested green.**
- Case 1, the PR head already contains the latest base and is green: merge directly, with no prepare, publish or CI.
- Case 2, the base moved but the merge is clean: publish the squashed commit and wait for all checks (O12).
- Case 3, resolved and regenerated: the same as case 2.
- A clean textual merge is never trusted on its own, because it misses semantic conflicts. | Owner, grill 12. A serial queue (D11) at about 5 min of CI per PR is acceptable at current volume. |
| O3 | **Fork PRs need "Allow edits from maintainers" ticked.** The bot pushes to the contributor's fork branch through that permission; there is no bot-made fork. Without the box ticked, the PR is refused at enqueue with an explanation. | Owner. S6 now only verifies that an **App installation token** counts as a maintainer for that checkbox. GitHub documents the checkbox for user-owned forks only; org-owned forks cannot tick it. |
| D20 | **The O2 hunk check reads git's own conflict regions, with `merge.conflictStyle=zdiff3`.** merge-tree writes each conflicted file into its result tree with markers. A region is accepted only if every line in its ours/base/theirs sections is a line of that stage's blob that holds nothing but one allow-listed top-level JSON member (name, value, comma). Lines are matched by text, and a text counts only if *every* line of that stage with that text is allowed, so position never has to be guessed. Every stage blob must be a strict JSON object (which also rules out marker-like lines), at most 1 MiB, mode `100644`. Plain `diff3` was tried first and is too coarse: for an add/add conflict it reports the whole file as one region, so even a roll-up-only add/add evicted. | `CodeCoverage.Git/MergeQueue/ConflictHunks.cs` (the full argument is in its doc comment). `MergeQueueResolverTests.An_add_add_conflict_on_the_roll_ups_only_resolves_to_the_base_side`: red with `diff3`, green with `zdiff3`, 2026-10-14. |
| D21 | **`attr.tree` = the empty tree on every git command, and `GIT_ATTR_SOURCE` is cleared.** git 2.47.1 (local) supports `attr.tree`. In a bare repo it does **not** read in-tree `.gitattributes` by default, but pointing `attr.tree` at the PR's tree makes `merge=union` silently merge a hand-authored file, so pinning the empty tree is the guard. The 2.50.1 floor in §4.3 stays: it is about the July 2025 CVEs, not about `attr.tree`, and M3 pins it in the writer image. | `MergeQueueResolverTests.An_in_repo_gitattributes_cannot_pick_a_merge_driver`: red (`-c attr.tree=pr`: `translations.json` merges cleanly) and green (the runner's empty tree: conflict, evicted). |
| D22 | **`GitRunner` moved to `apps/CodeCoverage/CodeCoverage.Git`, with the resolver in `CodeCoverage.Git.MergeQueue`.** GitMirror maps `GitMirrorOptions` onto `GitRunnerOptions` and keeps `transfer.fsckObjects` **off**: its treeless mirror is all promisor packs, and its behaviour must not change. The writer turns it on (`GitRunnerOptions.CheckReceivedObjects`). Per-command `-c` pairs may not override a hardened key or `protocol.*`. | Commit 514c6ee5. CodeCoverage.Git.Tests: 34/34 green. CodeCoverage.GitMirror.Tests: 68/68 green on a rerun. The first full run had one failure, `MirrorServiceTests.A_fetch_brings_new_commits…` ("cloned" where "fetched" was expected). Three isolated runs of that class and the full rerun were green, and the defect is not yet located. **Located: D26.** |
| D26 | **(M3, the GitMirror failure of D22) Located and fixed: on Windows the rename of a finished clone into place is refused while another process holds a file in it.** `MirrorService.CloneAsync` moved the partial directory with `Directory.Move` (`MirrorService.cs:207`). Windows refuses a directory rename while any handle without `FILE_SHARE_DELETE` is open on a file inside it, and an on-access scanner (Defender real-time protection is on here) opens the pack git has just written. The clone then failed (`failed`/`error`); the test never asserted its first fetch, so the *next* fetch cloned and the failure surfaced as "cloned" where "fetched" was expected, two steps from its cause. Fix: `MirrorPaths.MoveTree` retries the rename for up to 2 s (like `DeleteTree` already did), logging the retries; Linux renames ignore open handles, so production never hit it. The test now asserts its precondition (`cloned.Outcome == "cloned"`, with the sidecar's error lines as the message). | Evidence: the GitMirror project was run 14 times (two loops of 6 and 8) with the sidecar's log captured on every unexpected `failed`: 2 runs failed, each in a different test's *first* clone (`A_clone_mirrors…`, `At_returns_the_page…`), and the captured log was `System.IO.IOException: Access to the path '…\github\4002.git.partial-…' is denied. at System.IO.FileSystem.MoveDirectory … MirrorService.CloneAsync … line 207`. After the fix: 12 runs, no clone failure (one run failed on the diagnostic's own log file, since removed). `MirrorMoveTests.A_rename_refused_while_a_fresh_file_is_held_open_succeeds_once_the_handle_closes` reproduces it deterministically: a `FileShare.Read` handle on a file in the directory makes `Directory.Move` throw `IOException` (the control), and `MoveTree` succeeds after the handle closes, with retries > 0. No CPU burner was used. |
| D27 | **(M3) The writer's tests use a real smart-HTTP forge over `http://`, through the `AllowInsecureHttp` test seam, not `file://`.** The writer keeps `protocol.file.allow=never` in every mode, so a local bare remote by path is impossible by design. The tests reuse the mirror's `GitHttpServer` (Kestrel running `git http-backend` with Basic auth), with receive-pack on and the served repository configured like GitHub as far as the writer can tell: `uploadpack.allowFilter`, `uploadpack.allowAnySHA1InWant` (lazy blob fetches) and `receive.hideRefs=refs/pull`. With that, a fork's head (only `refs/pull/1/head`) forces the push to send blobs the blob-less clone fetched lazily, which is the hard case. | `CodeCoverage.GitWriter.Tests` (`_Infrastructure/Fixture.cs`, `GitHttpServer.cs`); `PrepareTests.A_clean_merge_of_a_fork_head_…` runs `fsck --strict` on the forge afterwards. Production accepts https only (`WireValidation.RemoteUrl`; a `file:` URL is a 400, `HardeningTests`). |
| D28 | **(M3) `write-tree --missing-ok` in a blob-less clone.** M2's resolver ran `write-tree` on a full clone; in the writer's `--filter=blob:none` clone, `write-tree` verifies every blob the tree names and tried to fetch them all ("could not fetch … from promisor remote"; without a credential it failed outright). The resolver and the publish step now pass `--missing-ok`: the only blobs they add are the stage-2 blobs the resolver already read and the blobs `git apply` fetched. | Red: 15 of 51 writer tests failed with exactly that message on the first run; green after. M2's tests (full clones) are unchanged and green. |
| D29 | **(M3) The git floor is a build-time gate in the writer's Dockerfile, with the distribution's git.** The local Docker daemon is not running, so the `aspnet:11.0` base image's git version could not be measured here. The Dockerfile therefore accepts git ≥ 2.50.1, upstream's patched maintenance releases of July 2025 (2.43.7, 2.44.4, 2.45.4, 2.46.4, 2.47.3, 2.48.2, 2.49.1, or later in those lines), or a Debian changelog naming CVE-2025-48384 (Debian backports into old version numbers), and **fails the build otherwise**. `code-coverage-image-check.yml` builds it on every pull request, so an unpatched base fails before merge. No source build, no backports by default. | The check's logic was exercised with sample versions: 2.47.1, 2.39.5 and 2.50.0 refused; 2.47.3, 2.43.7, 2.50.1, 2.51.0 and 3.0.0 accepted (scratchpad `gitfloor.sh`). **Open:** the first CI image build shows which branch passes. |
| D30 | **(M3, T4) The patch is validated by a strict parser before anything is cloned, then applied with `git apply --cached` on a throwaway index in the bare repository.** Verified first on git 2.47.1: `apply --cached` with `GIT_INDEX_FILE` works in a bare blob-less clone and fetches the preimage blobs it needs. The parser accepts only `diff --git` sections and refuses: text outside a section, any mode but 100644 (symlink, gitlink, executable), mode changes, binary patches, copies, quoted or whitespace paths, `.git`/`.github` components, `.gitmodules`/`.gitattributes`, any path (both ends of a rename) outside `regeneratedPaths`, more than 500 files or `MaxPatchBytes` (2 MiB). **Hunks are counted exactly like git**, because `git apply` would read a `--- a/x` line after a short hunk as a new, unvalidated file header. After applying, the trees are compared again (`unexpected-change`). Deletes and renames are allowed only inside `regeneratedPaths`. | `PatchValidatorTests` over real `git diff --binary` output between trees built with the CLI (symlink, submodule, executable, mode change, binary, copy, rename) plus hand-made attack shapes (hunk-count smuggling, a mail header, quoted paths, NUL, invalid UTF-8). `PublishTests`: out-of-allow-list, symlink, binary and mode-change patches are refused with no git started and the branch unchanged. |
| D31 | **(M3) Publish takes the author and message from the prepared commit and pushes the prepared commit itself when there is no patch.** The final commit is `commit-tree <prepared tree + patch> -p <prepared's only parent>`, so it is ONE commit on the base; `cat-file commit` gives the author's original date and offset. A publish fetches the PR head too and answers `branch-moved` before building anything when it moved; the lease catches a push GitHub has not yet reflected in `refs/pull/N/head`. Lease rejection (`[rejected] (stale info)` in `--porcelain`) is `branch-moved` / `queue-branch-moved`, a forge refusal (`[remote rejected]`) is `push-rejected`, never a 500. | `PublishTests.A_valid_patch_publishes_one_squashed_commit_…` (one parent = base, author name/email/date `+0200` preserved, bot committer), `…An_author_push_during_the_run_is_a_rejected_lease_not_a_failure`, `…A_head_that_already_moved_…`. |
| D32 | **(M3, S8) Job caps: memory and pids are the container's, disk and time are the process's.** One slot by default (`MaxConcurrentJobs` 1, at most 2, refused otherwise at startup), so the container's `mem_limit: 768m` (512 MB job + 256 MB host) and `pids_limit: 128` (64 + 64) are the job's; git runs with `pack.threads=1` and bounded pack caches. A watchdog measures the job directory every second and after each clone/fetch/merge (`too-large`, 1 GB); the job's 180 s wall clock kills the git process tree (`timeout`). A request that waits 30 s for a slot is `busy`. The root file system is read-only except the jobs volume and a 64 MB `/tmp`. Per-process `RLIMIT_AS`/`RLIMIT_NPROC` (via `prlimit`) were rejected: an address-space limit breaks git's pack mmaps, and NPROC counts every process of the uid. | `JobTimeoutTests`, `JobDiskCapTests`, `JobDiskFloorTests`, `JobSlotTests`; `tools/verify-backend-networks.test.mjs` asserts the compose limits. |
| D33 | **(M3, O19) The allow-list supports a whole-file entry, and O19's default is `AutoResolveRule.SparkGeneratedFiles`** (`modelHashes.json` and `securityPosture.txt`, any line, take-base). A whole-file entry skips the JSON and hunk checks but keeps the others: only `CONFLICT (contents)`/`(add/add)`, both sides regular `100644` files, each side ≤ 1 MiB. The key-scoped rule (O2) stays available. The writer's wire entry is `{ path, keys }` or `{ path, anyLine: true }`. | `MergeQueueResolverTests.Two_entities_inserted_into_the_same_gap_resolve_under_the_O19_whole_file_rules` (was `…_and_evict`: the key rule still evicts it, as the control; the whole-file rules resolve it to the base side), `A_whole_file_rule_…` (2), `PrepareTests.A_whole_file_entry_resolves_the_same_conflict`. CodeCoverage.Git.Tests 36/36. |

---

## 1. Problem

Two pull requests that each change the Spark model always conflict. For example, PR A adds `Person` + `People` and PR B adds `Order` + `Orders`. Both rewrite the roll-up hashes in `App_Data/modelHashes.json`:
- `modelHash`, `contextRoots` and `modelFiles` change on practically every pair of model PRs (`docs/model_sync_lifecycle_PRD.md:568-573`) **[verified]**.
- Usually `translations.json` conflicts too, because synchronize *appends* its `en` seeds at the end of the object (`libs/spark/.../TranslationsSeeder.cs:103-139`) **[verified]**.
- `security.json` and `programUnits.json` conflict when both PRs add rights or menu entries **[verified]**.

GitHub's native merge queue **evicts** a PR on any conflict and has no hook to resolve it. Neither do GitLab merge trains or Bitbucket's merge queue (§4.1) **[cited]**. As a result, every second model PR in a queue needs a human to:
1. rebase,
2. pick a side,
3. re-run `--spark-synchronize-model`,
4. force-push,
5. wait for CI again.

That is mechanical work. Both sides of the `modelHashes.json` conflict are *wrong* until the branch is rebased, and the documented fix is exactly "empty it, regenerate, stage" (`docs/model-hash.md:129-145`) **[verified]**. Spark's own model-sync PRD already names a merge queue as the intended caller of the headless synchronize, and leaves building one out of scope (`docs/model_sync_lifecycle_PRD.md:45,53`) **[verified]**.

## 2. Goal

CodeCoverage runs its own merge queue for a repository:
- It takes queued PRs one at a time per base branch.
- It squashes and rebases each onto the latest base.
- It resolves conflicts **only** in paths the repository has allow-listed, each with a declared strategy.
- It triggers a consumer-named CI workflow (`postRebaseActionName`) that regenerates the derived files.
- It folds the regenerated files into the PR, waits for the required checks, and squash-merges.

Any other conflict, a failed check, or a concurrent push by the author evicts the PR, with a visible reason.

The design is forge-neutral, behind `IForgeIntegration`. GitHub comes first because it is the only implemented forge (§3.1).

### Non-goals (genuinely not being done)
- **Running consumer commands on our infrastructure.** Regeneration runs in the consumer's own CI, on their minutes. This is the point of `postRebaseActionName`.
- **Resolving arbitrary semantic conflicts.** Only allow-listed paths are resolved, with mechanical strategies. Everything else is evicted.
- **Replacing GitHub's native merge queue on repositories that require it.** The repository turns that ruleset off (§4.1).

---

## 3. What exists today

### 3.1 Forge abstraction
- **The seam:** `CodeCoverage.Library/Forge/IForgeIntegration.cs:38-234`, with capabilities declared in `EForgeCapability {Statuses, Comments, Boards, CiIdentity, GitMirror}` (`:245-274`). A member the integration doesn't declare throws `NotSupportedException`, and a conformance test checks this **[verified]**.
- **Missing members:** merging a PR, updating or force-pushing a ref, creating a commit, dispatching a workflow, reading a workflow run or artifact, reading the required checks **[verified]**.
- **GitHub only:** `GitHubForgeIntegration.cs:54` declares `Statuses, Comments, GitMirror` **[verified]**. GitLab and Bitbucket are empty stubs that `Program.cs` does not call (`CodeCoverage.GitlabIntegration/Extensions/SparkBuilderExtensions.cs:43-51`, `CodeCoverage.BitbucketIntegration/...:33-38`) **[verified]**.
- **Tokens:**
  - `IGitHubInstallationService.GetInstallationTokenAsync(id, minRemaining)` returns a cached raw token for git (`libs/webhooks/MintPlayer.Spark.Webhooks.GitHub/Services/IGitHubInstallationService.cs:6-37`) **[verified]**.
  - `ForgeGitAccess` redacts the token in `ToString` (`IForgeIntegration.cs:284-295`) **[verified]**.

### 3.2 GitHub App permissions and events
- **Granted:** Contents R&W, Pull requests R&W, Checks R&W, Issues R, Projects R&W, Members R, Metadata R, Email R (`docs/code-coverage/hosting.md:357-366`) **[verified]**.
- **Missing permissions:**
  - **Actions: write**, to dispatch workflows and read runs and artifacts.
  - **Workflows: write**, needed whenever a pushed commit touches `.github/workflows/**`.
  - **Administration: read**, to read classic branch protection. This may be avoidable (S5).
- **Subscribed events:** Repository, Push, Pull request, Organization, Issues, Issue comment, Pull request review, Check run (`hosting.md:375-382`) **[verified]**.
- **Missing events:** `workflow_run`, `check_suite`.
- **Unknown events already arrive:** the webhook processor broadcasts every delivery as a catch-all `GitHubWebhookMessage` before it answers 200 (`libs/webhooks/.../SparkWebhookEventProcessor.cs:88-171`) **[verified]**. So new event types need only a subscription, not library work.

### 3.3 Messaging (the saga runtime)
- **Defaults:** `MaxAttempts=5`, backoff 5s/30s/2m/10m/1h, `ClaimTtl=5m` (`libs/messaging/MintPlayer.Spark.Messaging/SparkMessagingOptions.cs:40,64,90`) **[verified]**.
- **Delayed delivery and dedup:** `BroadcastOptions.Delay` and `DeduplicationKey` exist **[verified]**.
- **Checkpoints:** `ICheckpointRecipient<T>` resumes from a checkpoint **[verified]**.
- **Constraint:** compose's `stop_grace_period: 120s` must stay below `ClaimTtl`. So a git step must finish inside one claim, or be split into steps.
- **Outbox precedent:** `PullRequestFeedback`/`BuildFeedback` (State, Attempts, NextAttemptAtUtc, Error), drained by `PublishFeedbackCronJob` **[verified]**.
- **Self-echo precedent:** `ProjectAutomationRouter.cs:68-75,136-205` drops deliveries the app caused itself **[verified]**. The queue's own pushes, check runs and dispatches will echo back the same way.

### 3.4 Git on the server
- **The sidecar:** `CodeCoverage.GitMirror` (image `ghcr.io/mintplayer/codecoverage-git`) runs the git CLI from `Git/GitRunner.cs:99-144` **[verified]**.
- **Hardening already in place:**
  - empty read-only `core.hooksPath`
  - `GIT_CONFIG_NOSYSTEM`, global config `/dev/null`
  - https only; file and ext transports off
  - credentials passed only through `GIT_CONFIG_COUNT` + `http.extraHeader` in the process environment
  - secrets scrubbed from stderr (`GitRunner.cs:83-131,204-209`)
- **Why it can't be reused as is:** it keeps **treeless** mirrors (`--filter=tree:0`, `Mirror/MirrorService.cs:186`) and is **read-only by design** (threat model `docs/coverage_commit_graph_PRD.md` D1/D5) **[verified]**.
- **The app container has no git** (`CodeCoverage/Dockerfile:15-18`) **[verified]**.

### 3.5 Configuration precedent
- **`coverage.yml`:** `CodeCoverage/Feedback/CoverageYml.cs` holds per-repository config in the consumer repo. It is **read from the base ref, so a PR cannot rewrite its own policy**. A parse error is surfaced, not thrown (`PublishFeedbackRecipient.cs:65-69`) **[verified]**.
- **Stored settings:** the `Repository` entity holds settings edited in the UI (`DeleteBranchOnPrClose`, `Gate`, …). `Manage/RepoSettings` plus a live forge ownership check gates who may edit them (`RepoSettingsController.cs:20,51-54`) **[verified]**.

### 3.6 Spark's generated files: what is safe to resolve mechanically

| Path | Who writes it | Safe strategy | Evidence |
|---|---|---|---|
| `App_Data/modelHashes.json` | synchronize; never read by it | **take-base, then regenerate** | `ModelHashFile.cs:182-196`; `docs/model-hash.md:129-145` |
| `App_Data/securityPosture.txt` | `--spark-synchronize-security` | **take-base, then regenerate** | regenerable baseline |
| `App_Data/translations.json` | synchronize seeds `en` (add-only); humans write nl/fr | **json-union** (three-way, by key) | `TranslationsSeeder.cs:9-25,53-75` |
| `App_Data/security.json` | hand-authored; rights carry `key` GUIDs | **json-union**, arrays keyed by `key` | hand-authored, nothing regenerates it |
| `App_Data/programUnits.json` | hand-authored; units carry `id` GUIDs | **json-union**, arrays keyed by `id` | as above |
| `App_Data/Model/<Entity>.json` | generated structure plus hand edits plus the GUIDs others reference | **never**: evict | `ModelSynchronizer.cs:755,201,283,1056` mint new GUIDs for anything missing |

Two PRs that add *different* entities produce different `Model/*.json` files, so those never conflict. A same-entity conflict is a real conflict.

**A hazard to fix in Spark:** a model file holding conflict markers fails to load. The error is caught and only *printed* (`ModelSynchronizer.cs:695-698`). The entity is then regenerated with fresh GUIDs and the hand edits silently disappear **[verified]**. The queue never produces markers (§5.3), but the same trap catches a human doing a manual rebase. Synchronize must fail closed (M1).

**Synchronize runs headless:**
- Command: `dotnet run -- --spark-synchronize-model`, in **Debug**, because the description seeds are `[Conditional("DEBUG")]`.
- No RavenDB, and secrets are exempt in CodeCoverage (`Program.cs:29-34`).
- Exit codes: 0 in sync, 1 threw, 2 misconfigured, 3 drift (`docs/model-hash.md` ~97-125) **[verified]**.
- It is a fixed point from the second run onwards (`model_sync_lifecycle_PRD.md:276`) **[verified]**.

**Existing CI** already verifies (`--spark-verify-model`, `--spark-verify-security`) and never regenerates (`.github/workflows/pull-request.yml:93-142`) **[verified]**. That gate is exactly what tells us the post-rebase regeneration worked.

---

## 4. External facts the design rests on

### 4.1 Native queues
- **GitHub merge queue:**
  - Evicts on a conflict, a failed check or a timeout, and has no conflict hook **[cited]**.
  - A ruleset that *requires* the merge queue blocks direct merges unless the actor is on the bypass list. Mergify and Trunk both recommend turning that rule off, or giving the bot a bypass **[cited]**.
- **GitLab merge trains** (Premium) evict on conflict. `PUT …/rebase` runs on the server but **does not resolve conflicts** (`merge_error` "Rebase failed") **[cited]**.
- **Bitbucket Cloud's merge queue** is now GA (Standard and Premium). It evicts on conflict, does not support forks, and has no public enqueue API **[cited]**.
- **Prior art:** none of Mergify, Trunk, Graphite, Aviator, Kodiak, bors-ng, Zuul or Marge-bot auto-resolves conflicts or runs a consumer regeneration step after a rebase **[cited]**. That part is new.

### 4.2 GitHub specifics
- **Pushes with an installation token do trigger workflows.** They start `push` and `pull_request: synchronize`. Only pushes made with `GITHUB_TOKEN` are suppressed (docs.github.com, "GITHUB_TOKEN"; peter-evans/create-pull-request) **[cited]**.
  - This contradicts a line in the codebase investigation; the cited docs win, and S3 re-proves it.
  - **Corollary:** a commit that the consumer's workflow pushes with `GITHUB_TOKEN` starts **no** CI, so the required checks never appear on it. This shapes D4.
- **`workflow_dispatch`:**
  - Endpoint: `POST /repos/{o}/{r}/actions/workflows/{file}/dispatches` with `{ref, inputs ≤ 25}`; needs `actions: write`.
  - **Since 2026-02-19**, `return_run_details: true` returns `workflow_run_id`, so we can correlate the run without polling **[cited]**.
  - It takes a **branch**, not a SHA. Pass the expected SHA as an input and have the workflow refuse to run if the branch moved.
  - The workflow must exist on the default branch to be dispatchable **[inferred; S2]**.
- **Fork PRs:**
  - "Allow edits from maintainers" covers user-owned forks. Nothing documents an installation token pushing to a fork.
  - Mergify needed user impersonation for this and **deprecated it on 2026-04-21** **[cited]**.
  - Assume we can't push to fork branches (S6).
- **Merging:**
  - `PUT /pulls/{n}/merge` with `merge_method=squash` and `sha` returns 409 when the head moved and 405 when the PR isn't mergable **[cited]**.
  - **The async merge API went GA on 2026-10-01.** It is now recommended over the sync endpoint and supports rule bypass for actors allowed to bypass. The exact parameters aren't confirmed yet **[cited; S5]**.
- **Signed commits:**
  - Commits pushed by a bot are Unverified.
  - "Unsigned commits on the head branch can block a squash merge", even though GitHub signs the squash commit itself **[cited]**.
  - Commits created through the Git Data API by the App, with no custom author, are signed by GitHub **[cited]**.
- **Rate limits:** content-creating requests are capped at 80/min and 500/h. Merges, comments, check runs and dispatches all count **[cited]**. The queue runs at about 5 such calls per PR, so the cap isn't a constraint at our volume.

### 4.3 Git mechanics (CLI, git ≥ 2.50.1)
- **`git merge-tree --write-tree -z --messages <base> <head>`** works in a **bare** repo with no worktree. It prints the merged tree OID, the conflicted stages and messages whose conflict-type strings are stable, and exits 0 when clean and 1 on conflict **[cited]**.
  - A single three-way merge squashes and rebases in one step, and we squash-merge anyway.
- **`git replay`** gives no per-path resolution and is experimental. **LibGit2Sharp** has no ort merge and had five CVEs in 2026 **[cited]**.
- **Minimum version: git 2.50.1** (or a backport of the July 2025 batch: CVE-2025-48384, CVE-2025-48385 and others) **[cited]**.
- **No checkout:** a bare repo with no worktree removes the checkout, submodule and hook RCE class entirely.
- **Neutralising in-repo `.gitattributes`:** `-c attr.tree=<empty tree>` turns off its influence. Our own rules go in `info/attributes` **[cited; S1 re-verifies]**. **[Verified, D21]**: red with `attr.tree=<PR tree>`, green with the empty tree, on git 2.47.1.
- **Lease:** `push --force-with-lease=refs/heads/X:<sha>` is a compare-and-swap on the server **[cited]**.

---

## 5. Design (proposed)

### 5.1 Flow per queued PR (GitHub)

```
enqueue (label / comment / UI)
  │  authorize: the actor has write on the repo; the PR is open, not a draft, approved per rules, not a fork (D7)
  ▼
[1] PREPARE        in the git-writer sidecar (D2): partial bare clone, fetch base + PR head,
                   assert head == pinned SHA, merge-tree base⟵head, resolve allow-listed conflicts (§5.3),
                   commit-tree on base (author = PR author; committer = bot),
                   push to queue branch  mq/<base>/pr-<n>   (not the PR branch: CI must not run twice)
  │  non-allow-listed conflict ─► EVICT("conflict in <paths>")
  ▼
[2] REGENERATE     only when an auto-resolved path needs it (strategy take-base), or always if configured:
                   workflow_dispatch <postRebaseActionName> on ref mq/<base>/pr-<n>,
                   inputs {expected_sha, pr_number, queue_run_id}, return_run_details=true
                   the workflow regenerates and uploads a PATCH ARTIFACT; it does not push (D4)
  │  workflow_run completed → download artifact → validate (paths ⊆ allow-list ∪ regen-paths, size cap)
  │  failure / timeout / out-of-bounds patch ─► EVICT
  ▼
[3] PUBLISH        apply the patch as a tree edit on top of [1]: ONE final commit = base + PR net change + regenerated files
                   force-with-lease the PR branch (lease = the head SHA pinned at enqueue)
                   this push is made with the App token, so it triggers the consumer's normal PR CI (§4.2)
  │  lease rejected (the author pushed) ─► EVICT("branch moved during queue run")
  ▼
[4] AWAIT CHECKS   check_run / check_suite / workflow_run webhooks for the final SHA; the queue's own check run shows state
  │  a required check fails ─► EVICT (with retry budget, D9) · base moved meanwhile ─► back to [1] (no eviction)
  ▼
[5] MERGE          async merge API, squash, sha = final SHA (precondition); PR becomes "merged"
                   409/405 ─► back to [1] once; again ─► EVICT
  ▼
[6] next PR on this base
```

**Why there is a queue branch.** Pushing the bare rebase straight to the PR branch would start a full CI run on a commit that is *known* to fail `--spark-verify-model`, since its hashes are stale. That run would then be thrown away when the regenerated commit lands. Keeping steps [1] and [2] on `mq/…` means the consumer's PR CI runs **once**, on the final commit **[inferred; S3 measures]**.

### 5.2 Configuration: in the consumer's repo, read from the base ref

The queue extends the existing `coverage.yml` (§3.5) with a `mergeQueue:` section. The precedent applies: it is read from the **base** ref, so a PR cannot widen its own auto-resolve list.

```yaml
mergeQueue:
  enabled: true
  branches: [master]
  mergeMethod: squash            # v1: squash only
  enqueue:
    label: merge-queue           # also: comment "/queue", UI button
  postRebaseActionName: regenerate.yml        # workflow file; must declare workflow_dispatch
  postRebaseTimeout: 20m
  postRebaseWhen: auto-resolved  # auto-resolved | always | never
  regeneratedPaths:              # what the patch artifact may touch
    - "**/App_Data/modelHashes.json"
    - "**/App_Data/securityPosture.txt"
    - "**/App_Data/translations.json"
    - "**/App_Data/Model/*.json"
  autoResolve:
    - path: "**/App_Data/modelHashes.json"
      strategy: take-base        # either side is wrong; regeneration fixes it
    - path: "**/App_Data/securityPosture.txt"
      strategy: take-base
    - path: "**/App_Data/translations.json"
      strategy: json-union
    - path: "**/App_Data/security.json"
      strategy: json-union
      arrayKeys: { "rights": "key", "groups": "id" }
    - path: "**/App_Data/programUnits.json"
      strategy: json-union
      arrayKeys: { "*": "id" }
  checks:
    retryFlaky: 1                # D9
```

`take-base` paths imply `postRebaseActionName` is required. A config with `take-base` and no action is rejected at parse time and surfaced on the queue's check run.

### 5.3 The resolver (`merge-tree` post-processing; no merge drivers, no checkout)

1. Run `merge-tree --write-tree -z --messages refs/mq/base refs/mq/head`. Exit 0 → done. Any exit other than 0 or 1 → error.
2. **Evict** if any of these hold:
   - a conflicted path is not on the allow-list
   - a message type is anything other than `Auto-merging`, `CONFLICT (contents)` or `CONFLICT (add/add)` (so modify/delete, rename and directory/file conflicts all evict)
   - the exit code is 1 but there are no stage entries (directory-rename conflicts)
3. **take-base:** use the stage-2 OID (the base side).
4. **json-union:**
   - read stages 1, 2 and 3 (after a `cat-file -s` size cap) and three-way merge by object key, and by `arrayKeys` for arrays
   - one-sided changes and deletions win
   - a same-key, different-value clash **evicts**
   - write the result with `hash-object -w`
   - serialise exactly as Spark does: indented, UTF-8 without BOM, `\n` (S1 checks a no-op round trip)
5. Rewrite the tree through a throwaway `GIT_INDEX_FILE`: `read-tree`, then `update-index --index-info`, then `write-tree`. Then `commit-tree -p base`.

The output tree never contains conflict markers, so the Spark hazard in §3.6 can't come from the queue.

### 5.4 Forge abstraction

- **A new capability, `EForgeCapability.MergeQueue`,** with a separate narrow interface `IForgeMergeQueue`, kept out of the already wide `IForgeIntegration` (design-principles: a deep module, information hidden behind a small surface). Its members:
  - `GetMergeabilityAsync`
  - `GetRequiredChecksAsync`
  - `DispatchWorkflowAsync` → `runId`
  - `GetWorkflowRunAsync`
  - `DownloadArtifactAsync`
  - `MergeAsync(sha, method)`
  - `PublishQueueCheckAsync`
  - `CanActorEnqueueAsync`
- **Git pushes go through the git-writer sidecar,** fed a short-lived token in the request body. That follows the `SidecarCredential` precedent (`CodeCoverage/CommitGraph/CommitGraphSidecarClient.cs:24-158`) **[verified]**.
- **Conformance tests** extend the existing capability test.

### 5.5 State and runtime

- **Entities:**
  - `MergeQueue`: per (repository, base). Holds the ordered entries, a `Paused` flag and a lock holder.
  - `MergeQueueEntry`: the PR, the pinned head SHA, State, Attempts, the SHAs from each step (`preparedSha`, `finalSha`), `dispatchRunId`, the eviction reason, and timestamps.
  - Both follow the `PullRequestFeedback` outbox shape.
- **The saga:** `ICheckpointRecipient<MergeQueueStep>` on a dedicated `[MessageQueue(CoverageQueues.MergeQueue)]`.
  - Each step is idempotent and keyed by `(entry, step, sha)`, so a webhook that is replayed or arrives twice is harmless.
  - Waits use `BroadcastOptions.Delay` plus webhooks. There is no polling loop.
  - Step [1] must finish within `ClaimTtl` (S8).
- **Self-echo:**
  - Our own pushes come back as `push` and `pull_request.synchronize` with `after == finalSha` or `preparedSha`. These are ignored.
  - Any other `after` on a queued PR's branch is an author push, which evicts the PR.
- **One in-flight merge per base** (serial). This makes "require branches to be up to date" hold by construction.

### 5.6 The consumer side (Spark repositories)

**A reusable composite action, `MintPlayer/Spark/.github/actions/spark-regenerate`,** goes in the same PR, beside the existing `compile-ts-action` precedent:
- It checks out `expected_sha` and fails if the ref moved.
- It runs `dotnet build -c Debug`, then `--spark-synchronize-model` and `--spark-synchronize-security` for each configured app.
- It runs synchronize **twice** and requires the second run to change nothing (the fixed point, §3.6).
- It writes `git diff --binary` to a `merge-queue-patch` artifact.
- The workflow needs only `contents: read`.

A template `regenerate.yml` and a docs page `docs/code-coverage/merge-queue.md` complete it.

### 5.7 Security (O10): the bot must never merge more than what was authorised

The bot holds merge power that its triggers don't: a label, a comment and a button can all be pressed by people with less power, or forged. Each threat below has a mitigation and a test in M5/M6.

| # | Threat | Mitigation |
|---|---|---|
| T1 | **Forged actor.** A comment body says "/queue as @maintainer", or a request to the button forges the user. | The actor is only ever the **signed webhook's `sender`** (HMAC already verified, `SparkWebhookEventProcessor.cs:45-57`) or the authenticated CodeCoverage session mapped to its linked GitHub login. Never text from a comment, label name or request body. The button is a POST guarded by the existing antiforgery (PR #451). |
| T2 | **Insufficient role.** A Triage user applies the label; a fork author comments `/queue`. | O9: a `write` role check on the actor, at enqueue **and** right before merging. A refused label is removed and the check run says why. |
| T3 | **Swap after approval (TOCTOU).** The author pushes malicious code after review, before or during the queue run. | The head SHA is **pinned at enqueue**, and the prepare step fetches exactly that SHA. Ruleset 2's reviews are evaluated by GitHub on the final commit, and "dismiss stale approvals" plus "require approval of the most recent reviewable push" should be on. Any push whose `after` is not one of the bot's recorded SHAs evicts the PR (§5.5). The lease on the publish push and `sha` on the merge make both writes compare-and-swap. |
| T4 | **Content smuggled through the regenerate patch.** A compromised or PR-modified generator emits changes to source files. | The patch is applied only if every path is within `regeneratedPaths`, as read from the **base** ref. Patches containing modes, symlinks, submodules or binaries are rejected, and the size is capped. Anything else evicts. |
| T5 | **Secret theft through the dispatched workflow (a "pwn request").** `workflow_dispatch` on `mq/…` runs the **PR's** code, and the workflow file at **that ref**, with the base repository's context. A fork PR could edit `regenerate.yml` to read secrets, or exploit the build itself. | (a) Evict any PR that changes the `postRebaseActionName` file or anything under `.github/`. Dispatch uses the base version, which S2 proves. (b) The template and the composite action declare `permissions: contents: read` and reference **no** secrets, so the run can't leak what it never receives. (c) Document clearly that the regenerate workflow builds untrusted code: no secrets, no `id-token`, no self-hosted runners. (d) Dispatch only after an authorised actor (T2) has enqueued a PR that ruleset 2 shows as approved. |
| T6 | **Confused deputy through config.** A PR widens `autoResolve` or `regeneratedPaths` for itself. | The config is read from the **base** ref only (D3). |
| T7 | **The bot skips the repository's own rules.** | The App is exempt only from ruleset 1 ("Restrict updates"). Ruleset 2 (reviews, checks) has no bypass actors. At enable time the queue refuses a repository where the App can bypass ruleset 2 (S5). |
| T8 | **Hostile git content.** | §4.3: bare repo, no checkout or hooks, `attr.tree` empty, `transfer.fsckObjects`, git ≥ 2.50.1, job caps in a separate sidecar (D2). |
| T9 | **Token leakage.** | The token is per job, scoped to one repository, short-lived, passed only through the environment, and stderr is scrubbed. Existing `GitRunner` precedent. |

---

## 6. Proposed decisions (to grill)

| # | Proposal | Reason / evidence |
|---|---|---|
| D1 | **Squash, then rebase as one `merge-tree`.** No per-commit replay. | The result is squash-merged anyway; conflicts are resolved once instead of in every commit that touched the file (§4.3). |
| D2 | **A new git-writer sidecar** (`codecoverage-git-writer`). The read-only `codecoverage-git` stays read-only. `GitRunner` moves into a shared library. | The read-only threat model of `coverage_commit_graph_PRD.md` D1/D5 stays intact. The writer is the only component with push egress plus a write token, and holds them per job. |
| D3 | **Configuration in `coverage.yml`, read from the base ref.** | §3.5 precedent: a PR can't widen its own auto-resolve list. |
| D4 | **The post-rebase workflow returns a patch artifact. It does not push.** The bot applies the patch, after validating its paths, and pushes once with the App token. | A `GITHUB_TOKEN` push starts no CI (§4.2), so a workflow that pushes would leave the required checks missing forever. The artifact route needs only `contents: read` in the consumer workflow. It caps what the generator may touch at `regeneratedPaths`, and yields exactly **one** CI run on **one** final commit. This is the user's `postRebaseActionName` idea, with the push moved to the bot. |
| D5 | **Queue branch `mq/<base>/pr-<n>` for prepare and regenerate.** The PR branch is written once, at publish. | It avoids a guaranteed-red CI run on the stale-hash commit (§5.1). The branch is deleted after each entry. |
| D6 | **Model files (`Model/*.json`) are never auto-resolved.** | Taking a side re-mints GUIDs and drops hand edits (§3.6). They hold no hash line either (O2). |
| D7 | ~~v1 evicts fork PRs at enqueue~~ **[superseded by O3]** | — |
| D8 | **The native merge-queue ruleset must be off.** At enable time, the queue reads the rules and refuses with a clear message if the native queue is required. | §4.1. A bypass actor would also bypass the checks we rely on. |
| D9 | **Re-run a failed required check once (`retryFlaky: 1`) before evicting.** | Lesson from Trunk and Mergify. The budget is explicit and visible. |
| D10 | **GitLab and Bitbucket implement `IForgeMergeQueue` in this same PR** (the one-PR rule), as far as their integrations exist. Otherwise their milestone lands **with** the integration they need (#422). | Their integrations are stubs today (§3.1), with no auth or webhooks. Bitbucket Connect reaches end of support 2027-01-31, so build on access tokens or Forge (§4). **Owner decision needed (Q1).** |
| D11 | **Serial queue per base. No batching or speculative stacking.** | Our volume is a handful of PRs a day. Batching multiplies the regenerate step: each speculative stack would need its own regeneration. |

### Open questions for the owner
- **Q1:** Is GitLab/Bitbucket support in scope now (it requires building those integrations' auth and webhooks), or does the queue ship GitHub-only, with the forge-neutral interface ready?
- **Q2:** Who may enqueue: anyone with write on the repository (checked through the forge), or a CodeCoverage right such as `Manage/MergeQueue`?
- **Q3:** Enqueue trigger: label, `/queue` comment, UI button, or all three?
- **Q4:** Do any target repositories require signed commits? If so, the publish step uses the Git Data API, which gives GitHub-signed commits but no custom author (§4.2; S5).
- **Q5:** Accepting the new App permissions (Actions R&W, Workflows R&W) is per installation. Who accepts them on the MintPlayer org?

---

## 7. Spikes (run before any milestone)

Each spike records its numbers or its red/green result in this file.

| # | Question | How | Pass criterion |
|---|---|---|---|
| **S1** | Does the merge-tree resolver produce the tree a human would? | In a scratch clone of this repo, create the real pair: PR A adds `Person`+`People` and PR B adds `Order`+`Orders` to DemoApp, both synchronized. Merge A, then run §5.3 on B with git 2.50+. Then run synchronize on the result. | No markers anywhere. `json-union` on unchanged inputs round-trips byte-identically. After regeneration, `--spark-verify-model` and `--spark-verify-security` exit 0. The diff against a hand-resolved rebase is empty. `attr.tree` neutralises an in-repo `merge=`/`-merge` attribute (shown red then green). |
| | **Result (2026-10-14, git 2.47.1, Windows).** Run from a scratch clone of `feat/merge-queue` @5be5d01b with `apps/DemoApp`. `Person` already exists there, so PR A adds `Customer`+`Customers` and PR B adds `Order`+`Orders`, each followed by `dotnet run --project apps/DemoApp/DemoApp --no-launch-profile -- --spark-synchronize-model` (exit 0 each, 60 s and 54 s; the cold `dotnet build` of DemoApp in the clone took 12 min, most of it `npm install`). A merges first, then the resolver runs on `s1-base` vs `s1-b` in a bare clone. **(a) Today's append seeding:** merge-tree conflicts on `modelHashes.json` **and** `translations.json`, both seeds appended at the end of `entities`, and `DemoSparkContext.cs` auto-merges. Resolver: **evicted**, `ConflictOutsideAllowList [translations.json]`, in 959 ms. That is correct under O1, and it is **expected to disappear after O4**. **(b) O4 simulated** (`translations.json` sections key-sorted in base, A and B by a node script): merge-tree conflicts on `modelHashes.json` only. Resolver: **Resolved** (take-base), commit made with `commit-tree`, **0 markers** in the tree. Regenerating with synchronize changed only `modelHashes.json` (the 3 roll-ups plus the `Order.json` and `Order` lines). Then `--spark-verify-model` **exit 0** and `--spark-verify-security` **exit 0**. A hand merge (taking the PR side of `modelHashes.json`), regenerated the same way, gives **an empty tree diff** against the resolver's result. `attr.tree`: red then green (D21). The `json-union` criterion is moot (O1). **Caveat:** two entities that sort into the *same* gap of `files`/`entities` conflict on non-roll-up lines and evict (`MergeQueueResolverTests.Two_entities_inserted_into_the_same_gap_conflict_on_the_files_map_and_evict`). A human or the bot's own regenerate would fix it, but O2 forbids it. Scripts: scratchpad `s1.sh`, `s1b.sh`. | **Pass**, with (a) pending O4. |
| **S2** | Can we dispatch and correlate the post-rebase workflow? | A test repo with the App, dispatching on a non-default branch (`mq/…`) with `return_run_details`. | The run id is returned. A `workflow_run` webhook arrives for it. The artifact downloads with `actions: read`. It is confirmed whether the workflow file must exist on the default branch, and what happens when the `mq/` ref lacks it. |
| **S3** | Do App-token pushes trigger PR CI, and do `mq/` pushes stay quiet? | Push to the PR branch and to `mq/…` with an installation token. | `pull_request.synchronize` runs on the PR push. No `pull_request` run fires on `mq/…`; record whether `push:` triggers fire there and document a `branches-ignore: [mq/**]` recommendation. Record the effect of "dismiss stale approvals" on the rewrite. Confirm that a commit touching `.github/workflows/**` fails without `workflows: write`. |
| **S4** | Is the regenerate action deterministic on CI? | Run `spark-regenerate` twice on the same SHA. | The two patch artifacts are byte-identical. Record build plus synchronize duration (this sets `postRebaseTimeout`). |
| **S5** | Merging under rulesets. | Test repo: required checks, "require up to date", optionally "require signed commits". Use the async merge API. | Confirm the parameters and the `sha` precondition. Find out whether the required checks can be read with `metadata: read` (`GET /repos/{o}/{r}/rules/branches/{b}`) without Administration. Find out whether an Unverified bot head blocks the squash; if it does, prove the Git Data API publish path. |
| **S6** | Does an App installation token count as a maintainer for "Allow edits from maintainers" (O3)? | A user-owned fork with the box ticked, then the same fork with it unticked. | Push succeeds when ticked and is rejected when unticked. If the push fails even when ticked, O3 needs a maintainer user token instead; record that. |
| **S7** | Synchronize fails closed on an unparsable model file. | A unit test feeding a `Model/*.json` that contains conflict markers. | Red today: it prints and regenerates GUIDs. Green after M1: exit 1, and the file is untouched. **Result (2026-10-14): red → green.** `tests/MintPlayer.Spark.Tests/Builder/SynchronizeFailsClosedTests.cs` synchronizes a context, wraps the model file's `name` line in `<<<<<<<`/`=======`/`>>>>>>>` markers and runs `--spark-synchronize-model` again. Red before the fix: "Expected exitCode to be 1, but found 0". Green after: exit 1, stderr names `FcProbe.json`, and every file under `App_Data` is byte-identical to before the run. See D25. |
| **S8** | Resource budget on the VPS. | Run the prepare step on this repo and on the largest installed repo: partial bare clone (`--filter=blob:none`), fetch, merge-tree. | Wall time is well under `ClaimTtl` (5 min). RSS and disk per job are measured. Set the job memory, pids and disk caps from those numbers. |
| | **Result (2026-10-14, this repo, anonymous https, Windows dev machine, git 2.47.1).** Peak working set is the sum over all `git*` processes, sampled every 50 ms. `clone --bare --filter=blob:none`: **2.4–3.6 s, 48 MB, 3.0 MB on disk** (260 commits on master). Fetching a PR head (`refs/pull/514/head`): 1.7–1.9 s, 37 MB. A no-op base refresh: 1.3 s, 36 MB. `merge-tree` of master vs that PR: **0.2–0.4 s, 13–17 MB**. **A wide merge** (`--merge-base=master~250 master~130 master`, which lazily fetches every blob it needs): **14.7 s, 100 MB, disk 3.0 → 15.7 MB**. Lazy fetches are serial round trips. For comparison, a full `clone --bare` takes 12.3 s, 62 MB and 26.2 MB on disk. Caps for M3: memory **512 MB** per job (5× the worst observed), pids **64**, scratch disk **1 GB** per job, timeout well under `ClaimTtl`. A PR merge here stays in single-digit seconds. A repository whose PRs touch many files should **prefetch the blobs of the changed paths** (`git fetch` with the paths from `diff-tree`) or take a full clone instead of lazy fetching. The "largest installed repo" half is still open: it needs the production installations list. | **Pass** for this repo. |
| **S9** | GitLab and Bitbucket feasibility (only if Q1 = in scope). | GitLab: push with a project access token, `POST /pipeline` with variables, `PUT /merge` with `squash`+`sha`. Bitbucket: an access-token push, a custom-pipeline trigger, the squash merge (no `sha`, so re-read the head before merging). | Each forge's equivalents of steps [1]–[5] work end to end, or the gap is named. |

## 8. Milestones (after the spikes; one PR)

| # | Milestone |
|---|---|
| M1 | **Spark:** synchronize exits 1 on an unreadable model file (S7). Seed `translations.json` sorted rather than appended, so most translation conflicts disappear before the queue even sees them. Measure how often the `json-union` path is still needed afterwards. |
| M2 | **Shared git library:** move `GitRunner` and its hardening out of `CodeCoverage.GitMirror`. Add the resolver (§5.3) with unit tests over real merge-tree output fixtures (memory: parsers need REAL fixtures). **Done (514c6ee5):** `apps/CodeCoverage/CodeCoverage.Git` (`GitRunner`, `GitRunnerOptions`, `MergeQueue/MergeQueueResolver`, `ConflictHunks`, `MergeTreeOutput`, `AutoResolveRule.SparkModelHashes`) and `CodeCoverage.Git.Tests` (34 tests, all on real git repositories). Its decisions are D20–D22. The resolver throws `GitCommandException` for operational failures, so the saga can retry them, and returns `Evicted(reason, paths, detail)` for verdicts. |
| M3 | **The `codecoverage-git-writer` sidecar:** endpoints `prepare` and `publish` (apply the patch and lease-push), job caps from S8, its own egress network. Add it to `tools/verify-backend-networks.test.mjs` and docker-compose. Pin git ≥ 2.50.1. **Done:** `apps/CodeCoverage/CodeCoverage.GitWriter` (`POST /jobs/prepare`, `POST /jobs/publish`, `DELETE /jobs/queue-branch`, `GET /healthz`; `Jobs/WriterService`, `JobRunner`, `PatchValidator`, `WireValidation`) and `CodeCoverage.GitWriter.Tests` (real git over a local smart-HTTP forge), in the solution and nx. Compose service `coverage-git-writer` on `coverage-git-writer` (internal, app only) + `coverage-git-writer-egress`, image `ghcr.io/mintplayer/codecoverage-git-writer` built by the deploy and the image check. App side: `IGitWriterSidecar` (`CodeCoverage/MergeQueue/GitWriterSidecarClient.cs`, config `MergeQueue:GitWriterUrl`, registered by `AddCodeCoverageMergeQueue`); no saga yet (M5). Prepare also evicts a pull request whose net change touches `.github/**` (D12, `touches-workflows`) and answers `head-mismatch`/`base-moved`/`queue-branch-moved` as outcomes. Decisions D26–D33. Docs: `docs/code-coverage/hosting.md`. Open: the base image's git version is only known after the first CI image build (D29); a contract test pairing `GitWriterSidecarClient` with the real writer is not written (it would pull the app into the writer's test project). |
| M4 | **`IForgeMergeQueue` + `EForgeCapability.MergeQueue`,** implemented for GitHub. Add the conformance tests. Raise the App permissions (Actions R&W, Workflows R&W) and the events (`workflow_run`, `check_suite`) in `hosting.md` and the manifest. |
| M5 | **Entities, saga, self-echo filter and webhooks routing** (§5.5). Parse and validate `coverage.yml` `mergeQueue:`. Add the queue check run on the PR head, with position, state and eviction reason. |
| M6 | **Enqueue surfaces** (Q3), authorization (Q2), and a repository page panel showing the queue. |
| M7 | **The consumer side:** the `spark-regenerate` composite action, a template `regenerate.yml`, and dogfooding on this repository. Turn off the native queue if it is enabled (D8). |
| M8 | ~~GitLab/Bitbucket per Q1~~ **[superseded by O7: lands under #422]**. In its place: **org/repo settings and guidance.** The approval floor (O11), check exclusions (O12), the effective-config listing (O15), the permission-acceptance link (O17), and the "Optimal GitHub setup" page with redacted screenshots (O13). |
| M1+ | **Spark additions to M1:** sorted translation seeding (O4), and sorted `rights[]` in `--spark-synchronize-security` with an unsorted-array check in verify (O5). All app files are re-sorted once. **Done 2026-10-14 (with M1; D23–D25).** Before: `--spark-verify-security` exited 3 on 6 of 7 apps for order alone (SparkId has a `security.json` but `rights: []`); the posture baselines were already in sync. The re-sort ran through the commands themselves (`--spark-synchronize-security`, then `--spark-synchronize-model`) on all 7 apps. A script checked every `security.json` against `HEAD`: the rights are a pure permutation, the bytes before and after the array are identical, every right's line is unchanged, and the files stay CRLF with no BOM. DemoApp's one blank grouping line is dropped (D24). Every `translations.json` is deep-equal to `HEAD` once key order is ignored. `securityPosture.txt` did not change, because its rows do not follow file order. A second synchronize of both kinds left the tree byte-identical. Then CI's own commands (`dotnet run --project apps/X/X --no-build -c Debug -- --spark-verify-model` / `--spark-verify-security`, after `dotnet build -c Debug -p:EnableSpaBuilder=false`) exited **0 for all 7 apps on both**. One side finding: synchronize also moved `"agentPermissions"` after `"breadcrumb"` in 4 HR and 10 SeoTester model files. Master had them in hand-written order, so it was not at synchronize's fixed point, and the queue's regenerate step would have produced that diff on every PR. It is committed separately. The M1 item "measure how often `json-union` is still needed" is moot: O1 dropped `json-union`. |
| M9 | **Docs:** `docs/code-coverage/merge-queue.md`, plus links from `docs/model-hash.md` "Merge conflicts" and the README. |
| M10 | **The test sweep, then deploy:** the sidecar image and the App permission change are accepted on the MintPlayer org. End-to-end: the S1 pair is queued and both land without a human. |

## 9. Risks

- **A wrongly resolved file gets merged.** Mitigations: only allow-listed paths, `json-union` evicts on any clash, regeneration plus the existing verify gates must be green, and the result is squash-merged as one reviewable commit.
- **A malicious PR or generator.** Mitigations: the config is read from base, the patch paths are validated against `regeneratedPaths`, git runs in a hardened sidecar without checkout, and the token is short-lived and scoped to one repository.
- **Rewriting a contributor's branch.** The PR branch is rewritten into one squashed commit, and its history is lost on the branch. That is acceptable because the merge squashes anyway. The author is preserved as `GIT_AUTHOR_*`, and the other commit authors become `Co-authored-by:` trailers. Approvals may be dismissed if the repo dismisses stale approvals (S3).
- **CI cost.** Each queued PR that auto-resolved costs one regenerate run plus one PR CI run. That is less than what a human pays today: their own rebase CI run, plus a re-run after they forget to regenerate.
