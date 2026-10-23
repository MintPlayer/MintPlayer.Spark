# PR size labels (#527) — implementation plan

**PRD:** [coverage_pr_size_labels_PRD.md](coverage_pr_size_labels_PRD.md) · **Branch:** `feat/issue527-pr-size-labels`
· **one pull request**, which also carries the coverage-uploads guide move (PRD D10).

## Status

Grill done (2026-10-23): Q1–Q4 are answered and folded into the PRD (D1, D4.3, D5, D7, D10). **Spikes S1–S5 are done
(2026-10-24)**, and each result is written into the PRD with its evidence. Two of them change the plan: GitLab is out
of scope (S4, so no M6b), and both label writers share a dedicated serial queue (S5, PRD D9). **M1 is next.**

Tests run **once, at the end** (M10): the CodeCoverage.Tests classes the change touches, and the ClientApp specs. No
interim gate runs.

## Spikes

Each spike is time-boxed and answers one question. A spike that cannot answer its question within the box stops and
reports what it found.

### S1 — GitHub label behaviour, measured (≤ 1 h)

Use a scratch repository under the owner's account, with the app's installation token or a PAT the owner runs with `!`.
Measure:

1. `PATCH /labels/{name}` with `new_name`: does an open **and** a closed PR still carry the renamed label? (D3 relies on
   it.)
2. `DELETE /labels/{name}`: is the label removed from closed PRs too? (The Remove labels prompt, D4.3, says so;
   confirm the wording.)
3. The label name length limit: create names of 50, 100 and 256 characters, and record the 422 message.
4. Is a label name match case-insensitive on create? Create `size/m` while `size/M` exists.
5. Pacing: create and delete 60 labels back-to-back. Is there a secondary-limit 403, and how long is its `retry-after`?
   This sets D4.2's fan-out throttle.

Record the results in PRD D2/D3/D4. Delete the scratch repository afterwards.

**Done 2026-10-23** in the browser, on scratch repository `PieterjanDeClippel/pr-size-labels-scratch`, with PR #1 open
and PR #2 closed. Results are in PRD D2, D3 and D4.2.

1. A rename keeps the label on the open and the closed PR, and the old name 404s.
2. A delete removes the label from both.
3. Name ≤ 50 characters (the UI's `maxlength`); description ≤ 100 (server error at 101).
4. Names are case-insensitive: "Name has already been taken".
5. Pacing was **not measured**, because there is no API token in the browser. D4.2 uses GitHub's documented limits.

New finding: **archived labels** (PRD D3). Open for M5: the API's response when adding an archived label.

### S2 — Where the line counts come from (≤ 30 min, code reading plus one GraphQL call)

- Does Octokit.GraphQL's `pr.Files(...).AllPages()` (the reference's `:62-65`) work against the
  `CreateGraphQLConnectionAsync` connection as it is? Is `changedFiles` available in the same query, for the 3 000-file
  fallback?
- Cost of a 1 000-file PR: count pages and measure the time.
- Decide: fetch in the applier (the PRD default), or extend `PullRequestUpdated` with `Additions`/`Deletions`/
  `ChangedFiles` from the webhook payload as a cheap pre-check. `ForgeEventContractTests` must then cover the new
  fields.

**Done 2026-10-24.** Results are in PRD D5.

- `PullRequestUpdated` gains `Additions`, `Deletions` and `ChangedFiles` from the webhook.
- A PR whose raw total already maps to the lowest row is labelled without a fetch.
- Above 3 000 files, the totals are used.
- Otherwise, the files come from GraphQL, which carries no patch payload. REST took 19.8 s for 2 862 files.

### S3 — The scale and ignore lists edit well on the Account form (≤ 1 h)

- Is there an ng-spark renderer for a hex colour? If not, decide between a plain string field with a swatch next to it
  (a small display renderer) and a colour input. Do not build a general colour picker for this.
- An `int` column (`MinLines`) and an enum lookup column (`Mode`) inside an `AsDetail` `editMode: inline` list. One
  already exists (`DependencyRule.MaxUpdate`, `DependencyRule.cs:37`), so confirm the int case only.
- The validation error from `BeforeSave` must show the row. Check how a `DependencyRule` error reports today.

**Done 2026-10-24**, by code reading in ng-spark and Spark. The results are in PRD D2.

- **Colour:** a `System.Drawing.Color` property with dataType `color`. `input-type.pipe.ts` maps it to
  `<input type="color">` on the form and in an inline cell, and `spark-grid-cell.component.html:24-31` shows a swatch.
  Spark stores `#rrggbb`. Nothing new is built, and hex validation goes away.
- **`MinLines`:** an `int` inline cell renders as `<input type="number">`. Correction to this spike's own premise:
  `DependencyRule.MaxUpdate` (`DependencyRule.cs:38`) is an **enum** (`EDependencyThreshold`), not an int, so it is the
  precedent for `Mode` (a lookup, rendered as `bs-select`) and not for `MinLines`. No existing inline int was found,
  but the number path is the same pipe the form uses.
- **Row errors:** `SparkValidationException(message, "PrSizeLabelScale[i].MinLines")` lands on that cell
  (`spark-po-form.component.ts:663-676`, `SparkAddOnEndpoints.cs:115-116`). M10 checks this in the browser.

### S4 — GitLab merge-request labels: in this PR or not (≤ 45 min, code reading)

- Does `CodeCoverage.GitlabIntegration` already produce `PullRequestUpdated` for MR events, and does it have an
  authenticated client with label scope?
- GitLab labels are project- or group-level, and `PUT /projects/:id/labels` renames.
- Estimate the cost. If it is ≤ ½ day, it is in (M6b). Otherwise it goes into the PRD's Out of scope **with the
  reason**, not as a follow-up.

**Done 2026-10-24: GitLab is out of scope** (PRD D8 and Out of scope). `CodeCoverage.GitlabIntegration` is an empty
stage-1 stub that is not called from `Program.cs`. It has no client, no sign-in, no webhook normaliser and no
`PullRequestUpdated`, so labels would carry the whole GitLab port. M6b is dropped.

### S5 — One writer per repository (≤ 30 min, code reading)

The applier (on `spark-forge-all`) and the reconciler both write `Repository.PrSizeLabelState`.

- Does Spark messaging have a per-key lane (memory: lane attributes generator)? Can both message types share a
  repository key?
- If not: optimistic concurrency (`session.Advanced.UseOptimisticConcurrency`) plus messaging retry. Check that a
  `ConcurrencyException` is retried, not dead-lettered.
- Decide D9.

**Done 2026-10-24**, by code reading in `libs/messaging`. The decision is in PRD D9.

- There are no per-key lanes. There is one lane per `[MessageQueue]` name, with one strictly serial pump
  (`MessageQueueRouter.cs:106-131`, `MaxConcurrency` default 1, and CodeCoverage sets none).
- Both writers go on a dedicated queue, `spark-pr-size-labels`. The `spark-forge-all` recipient only republishes
  `ApplyPrSizeLabelMessage` there. Sharing `spark-forge-all` was rejected: a paced account-wide reconcile would stall
  every other forge event.
- A failed handler, a `ConcurrencyException` included, is retried up to `MaxAttempts` = 5 (`SparkMessagingOptions.cs:40`).
  Optimistic concurrency on the Repository save stays as a backstop.

## Grill — done 2026-10-23

| Q | Answer | Lands in |
|---|---|---|
| Q1 | Repository extra ignore rules after the account's; path-level, last match wins; `!` = count fully | M1, M3 |
| Q2 | Disable prompts Remove / Keep / Cancel through `Retry.Action` on a normal save | M7 |
| Q3 | Neutral "PR size" check run with the breakdown, also on failure | M6 |
| Q4 | Coverage-setup link only while the repository has no coverage | M9 |

Anything a spike surfaces that changes a decision goes back to the owner before M1.

## Milestones

### M1 — Entities, lookups, defaults

- `Account.PrSizeLabels` (bool, `[IgnoreForIndex]`)
- `Account.PrSizeLabelScale : List<PrSizeLabel>`, initialised to the six default rows
- `Account.PrSizeIgnoreRules : List<PrSizeIgnoreRule>`, initialised to the PRD D5 defaults
- `[ValueObject] PrSizeLabel`, `[ValueObject] PrSizeIgnoreRule` with `[ValueKey] Id` (`DependencyRule.cs` shape)
- `EPrSizeLabelPolicy` + `PrSizeLabelPolicy : TransientLookupReference<…>` (copy `DeleteBranchPolicy.cs`), and
  `EPrSizeIgnoreMode` with its lookup
- `Repository.PrSizeLabels : EPrSizeLabelPolicy`
- `Repository.PrSizeIgnoreRules : List<PrSizeIgnoreRule>`, empty by default, applied after the account's (PRD D5)
- `Repository.PrSizeLabelState : List<ManagedLabel>`, with no model attribute (internal state, never shown)
- `Repository.ResolvePrSizeLabels(repository, account)` returning `(bool, Source)`, like the resolvers at
  `Repository.cs:265-290`
- **Migration** `M_<ts>_BackfillPrSizeLabelDefaults`: existing Account documents get the default scale and ignore rules.
  Repositories need nothing, because the enum's default is `Inherit`. Check this against the stored-document trap in
  `coverage_branch_deletion_setting_PRD.md` §Migration: a missing field must deserialize as `Inherit`, which it does as
  enum value 0, but **assert it in a test**.
- Model JSON: `Account.json` gets a "PR size labels" group; `Repository.json` gets the override; `PrSizeLabel.json` and
  `PrSizeIgnoreRule.json` are new. Regenerate the model hash.

### M2 — Validation

`AccountActions.BeforeSave` applies the D2 rules plus the ignore rules (a non-empty glob that `PathGlob` accepts). It
refuses enabling with an empty scale, and every message names the offending row. Generated descriptions are filled in
here.

### M3 — The size calculator (pure)

`PrSizeCalculator.Compute(files, changedFiles, totals, ignoreRules) → (int size, breakdown)` and
`PrSizeScale.LabelFor(size) → row`. The breakdown (per file: counted / ignored / deletions only, plus the matching rule)
feeds the check run (M6).

- The rule list is the account's followed by the repository's. Per path, the last matching rule wins, and `!` counts
  the file fully.
- Above 3 000 files, use the totals.
- Unit tests port the reference's cases (lockfiles, `.md`, `*Tests/` additions, net deletions).

### M4 — Forge abstraction

- `EForgeCapability.PullRequestLabels`
- `IForgeLabels`: `ListRepositoryLabels`, `CreateLabel`, `UpdateLabel(oldName, new)`, `DeleteLabel`, `ListPullRequestLabels`,
  `AddLabel`, `RemoveLabel`, `ListPullRequestFiles`
- Resolve it through `IForgeIntegrationResolver`. Move `AddLabelAsync`/`RemoveLabelAsync` off `IForgeMergeQueue` onto
  `IForgeLabels` **only if** the merge queue can take the dependency cleanly. Otherwise share the GitHub implementation
  and leave the merge queue's interface alone (minimal diff).

### M5 — GitHub implementation

- REST for label definitions and for add/remove on the PR, as the merge queue does.
- GraphQL for files, per S2.
- 403 maps to "issues=write or pull_requests=write", like `GitHubForgeMergeQueue.cs:978-1005`.
- 404 on update or delete is a distinct result, so the reconciler can create.

### M6 — Reconciler + applier

- `PrSizeLabelReconciler.ReconcileAsync(repository, account)` implements the D3 table and writes state only after
  GitHub confirms.
- `PrSizeLabelRecipient : IRecipient<ForgeWebhookMessage<PullRequestUpdated>>`:
  1. Load the repository and, when its policy is `Inherit`, the account by point load. Use no `GetOrCreateAccount` (the
     same rule as the branch-deletion consumer).
  2. Resolve the setting, and return if the repository is off or the forge lacks the capability.
  3. Reconcile when the state differs from the scale.
  4. Create the "PR size" check run `in_progress` through `IForgeFeedbackPublisher` (PRD D7). It is idempotent by
     name and head SHA, so a retry updates it rather than adding a second.
  5. Compute the size, then apply the label exclusively (D6).
  6. In a `finally`, complete the check run **neutral**. On success the title is `size/M · 87 lines changed` and the
     breakdown table is capped at about 50 rows. On failure the title is "Size label not applied", with the reason.
- Register it, and update `RegistrationInventoryTests` / `ForgeWebhookDocsTests`.
- Per S5 (PRD D9), the work above runs in `ApplyPrSizeLabelHandler` on queue `spark-pr-size-labels`. The
  `IRecipient<ForgeWebhookMessage<PullRequestUpdated>>` on `spark-forge-all` only republishes
  `ApplyPrSizeLabelMessage(RepositoryId, PrNumber, HeadSha, Additions, Deletions, ChangedFiles)`, and the reconciler
  message uses the same queue. Pin the queue with `[MessageQueue]` on both message records.

### M7 — Settings-change fan-out and disable

- `AccountActions.IAfterSaveCommitted`: when the scale changes, publish `ReconcilePrSizeLabelsMessage` per repository
  with state, throttled per S1.5.
- **On disable** (PRD D4.3), `OnBeforeSaveAsync` in `AccountActions` and `RepositoryActions` raises
  `manager.Retry.Action` with Remove labels / Keep labels / Cancel.
  - Triggers: the account goes true → false via `IsValueChanged`, or a repository's effective value becomes off.
  - Cancel throws `SparkCancelException`, as `CarActions.cs:115-145` does.
  - A scoped holder carries the answer to `IAfterSaveCommitted`. Only Remove publishes `RemovePrSizeLabelsMessage`
    per affected repository that has state. The handler deletes the managed labels and clears the state.
  - The repository-scoped wording names that one repository instead of "N repositories".
- **Test:** HTTP end to end through `OwnerPrivacyHostFixture` in CodeCoverage.Tests, following
  `DependencyOnboardingTamperEndToEndTests.cs:315-352`. There is no browser E2E, because CodeCoverage is not in
  `E2E_APPS`.

### M8 — Edit surface and access

- The Account form group and the Repository override use the effective-value hint from M1's resolver.
- Bitbucket repositories show the setting disabled, with the reason.
- **FR10 test:** an anonymous and a non-manager read of Account and Repository show none of the new attributes, and a
  non-manager write is refused. The deny-by-default redaction (`OwnerSettings.cs:41-57`) should already cover this; the
  test proves it.

### M9 — Guides (PRD D10)

1. **`pages/coverage-setup/`**: move the examples, snippets and forge tabs out of `repo-setup-panel.component.ts`.
   - The snippets go into `coverage-setup.snippets.ts`, with a snippets spec that asserts the generated YAML is
     identical to what the panel produced for the same `baseUrl` (FR13).
   - Route `coverage/setup`.
   - A Guides program unit at order 0, with a new id. The other units keep their ids and orders shift.
   - Translations.
   - The Repository extras (`po-detail-page.component.ts:70`) replace `<app-repo-setup-panel />` with the link, shown
     only while the repository has no coverage yet (PRD D10). Use a signal the panels already load; verify which one
     here.
   - Delete the panel and its spec.
2. **`pages/pr-size-labels-setup/`**: the guide content listed in PRD D10.
   - Route `pr-size-labels/setup`, a Guides unit, translations.
   - Links from the Account group and the Repository override.
3. Update the comment block at `programUnits.json:30-33` to list all five guides.
4. Use ng-bootstrap components, not plain bootstrap classes, and styles in a sibling `.scss`.
5. Before any screenshot for the PR: scrub the page first.

### M10 — Docs, then the one test sweep

- `docs/code-coverage/pr-size-labels.md` (feature doc)
- `README.md` features list
- `hosting.md`: no permission or event change, and say so explicitly next to the Issues/Pull requests rows
- `forge-webhooks.md`: the new consumer of `PullRequestUpdated`
- Mark the PRD Implemented and add a Status section here with what landed.
- **Tests, once:** the new and touched CodeCoverage.Tests classes, using `StubGitHub` (real Octokit against a stub
  handler, as `GitHubForgeMergeQueueTests` does) and `CoverageRavenTest` + `RecordingMessageBus` for the recipient, plus
  the ClientApp specs for the two guide pages. Run with the Developer licence (`RAVENDB_LICENSE`). Then push and let the
  PR's CI be the full sweep.
- **Live test, locally.**
  - Every repository in the owner's account has the CoverageDevelopment GitHub App installed, and its webhooks reach
    a locally running CodeCoverage.
  - Run the app (`dotnet run --launch-profile https`), turn the labeller on for the owner's account, create a scratch
    repository, and open PRs against it.
  - Watch the labels get created and applied, and the neutral "PR size" check run.
  - This also settles what S1 could not measure through the UI:
    - the API's response when adding an **archived** label
    - the label-name limit through REST
    - GraphQL file-listing time
    - pacing on a scale change
  - Delete the scratch repository afterwards. The owner confirms the sudo prompt.

## Test list (written during the milestones, run at M10)

| Area | Cases |
|---|---|
| Calculator | reference parity, `!` re-include, last-rule-wins, net-deletions floor at 0, >3 000 files fallback, repository rule after account rule, repository `!` overrides an account ignore, breakdown names the matching rule |
| Scale | label boundaries on the pascalgn defaults (9/10, 999/1000), single-row scale, generated descriptions |
| Validation | duplicate name (case), duplicate threshold, no 0 row, a colour with alpha is sent as 6 digits, 101-char description, the error lands on `PrSizeLabelScale[i].Col`, enable with empty scale |
| Reconciler | create, adopt, rename keeps id, rename of a hand-deleted label → create, row removed → delete, user `size/foo` untouched, partial failure converges |
| Recipient | off → no calls; Inherit/Enabled/Disabled × account on/off; already-correct label → no write; swap S→M removes S only; Bitbucket → no-op; archived target label → no label, neutral check run names it, no fallback label |
| Check run | one neutral "PR size" run per head SHA; a retry updates it, never a second; a 403 completes it neutral with the reason; the breakdown is capped at 50 rows + "and N more" |
| Fan-out | scale change → one message per repository *with state*; unchanged save → none |
| Disable prompt (HTTP) | true → false plus another changed attribute → 449; resubmitting Keep saves both and publishes nothing; resubmitting Remove saves both and publishes the removal; Cancel stores nothing; an unchanged save does not prompt |
| Access (FR10) | anonymous/non-manager read redacted; non-manager write refused |
| Guides | snippets parity for `/coverage/setup`; both routes anonymous; Guides group lists five units; the Repository link shows without coverage and hides with it |
| Migration | missing `PrSizeLabels` on Repository reads `Inherit`; accounts get the default scale exactly once |

## Deliberately not doing

PRD § Out of scope applies. In addition:

- No `label` webhook subscription (PRD D4).
- No commit status. The check run replaces it (D7), so no permission change is needed.
- No custom action and no read-only attribute for disabling. It is a normal save with a retry prompt (D4.3).
- No browser E2E for the prompt.
