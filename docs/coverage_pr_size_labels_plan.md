# PR size labels (#527) — implementation plan

**PRD:** [coverage_pr_size_labels_PRD.md](coverage_pr_size_labels_PRD.md) · **Branch:** `feat/issue527-pr-size-labels`
· **one pull request**, which also carries the coverage-uploads guide move (PRD D10).

## Status

Not started. **The spikes run first.** Each one ends with a recorded result and the decision it settles, written into
the PRD with its evidence. Then the grill on Q1–Q4, then the milestones.

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
2. `DELETE /labels/{name}`: is the label removed from closed PRs too? (This is what Q2's risk is about.)
3. The label name length limit: create names of 50, 100 and 256 characters, and record the 422 message.
4. Is a label name match case-insensitive on create? Create `size/m` while `size/M` exists.
5. Pacing: create and delete 60 labels back-to-back. Is there a secondary-limit 403, and how long is its `retry-after`?
   This sets D4.2's fan-out throttle.

Record the results in PRD D2/D3/D4. Delete the scratch repository afterwards.

### S2 — Where the line counts come from (≤ 30 min, code reading plus one GraphQL call)

- Does Octokit.GraphQL's `pr.Files(...).AllPages()` (the reference's `:62-65`) work against the
  `CreateGraphQLConnectionAsync` connection as it is? Is `changedFiles` available in the same query, for the 3 000-file
  fallback?
- Cost of a 1 000-file PR: count pages and measure the time.
- Decide: fetch in the applier (the PRD default), or extend `PullRequestUpdated` with `Additions`/`Deletions`/
  `ChangedFiles` from the webhook payload as a cheap pre-check. `ForgeEventContractTests` must then cover the new
  fields.

### S3 — The scale and ignore lists edit well on the Account form (≤ 1 h)

- Is there an ng-spark renderer for a hex colour? If not, decide between a plain string field with a swatch next to it
  (a small display renderer) and a colour input. Do not build a general colour picker for this.
- An `int` column (`MinLines`) and an enum lookup column (`Mode`) inside an `AsDetail` `editMode: inline` list. One
  already exists (`DependencyRule.MaxUpdate`, `DependencyRule.cs:37`), so confirm the int case only.
- The validation error from `BeforeSave` must show the row. Check how a `DependencyRule` error reports today.

### S4 — GitLab merge-request labels: in this PR or not (≤ 45 min, code reading)

- Does `CodeCoverage.GitlabIntegration` already produce `PullRequestUpdated` for MR events, and does it have an
  authenticated client with label scope?
- GitLab labels are project- or group-level, and `PUT /projects/:id/labels` renames.
- Estimate the cost. If it is ≤ ½ day, it is in (M6b). Otherwise it goes into the PRD's Out of scope **with the
  reason**, not as a follow-up.

### S5 — One writer per repository (≤ 30 min, code reading)

The applier (on `spark-forge-all`) and the reconciler both write `Repository.PrSizeLabelState`.

- Does Spark messaging have a per-key lane (memory: lane attributes generator)? Can both message types share a
  repository key?
- If not: optimistic concurrency (`session.Advanced.UseOptimisticConcurrency`) plus messaging retry. Check that a
  `ConcurrencyException` is retried, not dead-lettered.
- Decide D9.

## Grill

Before M1, put Q1–Q4 and anything the spikes surfaced to the owner, one at a time. Record each answer as a decision in
the PRD, with "owner, <date>" as its evidence.

## Milestones

### M1 — Entities, lookups, defaults

- `Account.PrSizeLabels` (bool, `[IgnoreForIndex]`)
- `Account.PrSizeLabelScale : List<PrSizeLabel>`, initialised to the six default rows
- `Account.PrSizeIgnoreRules : List<PrSizeIgnoreRule>`, initialised to the PRD D5 defaults
- `[ValueObject] PrSizeLabel`, `[ValueObject] PrSizeIgnoreRule` with `[ValueKey] Id` (`DependencyRule.cs` shape)
- `EPrSizeLabelPolicy` + `PrSizeLabelPolicy : TransientLookupReference<…>` (copy `DeleteBranchPolicy.cs`), and
  `EPrSizeIgnoreMode` with its lookup
- `Repository.PrSizeLabels : EPrSizeLabelPolicy`
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

`PrSizeCalculator.Compute(files, changedFiles, totals, ignoreRules) → int` and `PrSizeScale.LabelFor(size) → row`.

- Ignore before IgnoreAdditions, the last matching rule wins, and `!` re-includes.
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
  4. Compute the size, then apply the label exclusively (D6).
- Register it, and update `RegistrationInventoryTests` / `ForgeWebhookDocsTests`.
- (M6b, only if S4 says so) GitLab.

### M7 — Settings-change fan-out and disable

- `AccountActions.IAfterSaveCommitted`: when the scale changes, publish `ReconcilePrSizeLabelsMessage` per repository
  with state, throttled per S1.5.
- On disable (account off, or repository set to `Disabled`), act per Q2.
- On the Repository side, the override changes through `RepositoryActions`, with the same hook.

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
   - The Repository extras (`po-detail-page.component.ts:70`) replace `<app-repo-setup-panel />` with the link, per Q4.
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

## Test list (written during the milestones, run at M10)

| Area | Cases |
|---|---|
| Calculator | reference parity, `!` re-include, last-rule-wins, net-deletions floor at 0, >3 000 files fallback |
| Scale | label boundaries on the pascalgn defaults (9/10, 999/1000), single-row scale, generated descriptions |
| Validation | duplicate name (case), duplicate threshold, no 0 row, bad hex, 101-char description, enable with empty scale |
| Reconciler | create, adopt, rename keeps id, rename of a hand-deleted label → create, row removed → delete, user `size/foo` untouched, partial failure converges |
| Recipient | off → no calls; Inherit/Enabled/Disabled × account on/off; already-correct label → no write; swap S→M removes S only; Bitbucket → no-op |
| Fan-out | scale change → one message per repository *with state*; unchanged save → none |
| Access (FR10) | anonymous/non-manager read redacted; non-manager write refused |
| Guides | snippets parity for `/coverage/setup`; both routes anonymous; Guides group lists five units |
| Migration | missing `PrSizeLabels` on Repository reads `Inherit`; accounts get the default scale exactly once |

## Deliberately not doing

PRD § Out of scope applies. In addition: no `label` webhook subscription (PRD D4) and no commit status (D7, pending Q3).
