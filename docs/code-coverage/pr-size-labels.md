# CodeCoverage — PR size labels

CodeCoverage labels every pull request with its size, `size/XS` to `size/XXL`, from the lines it changes, and posts a
neutral **PR size** check run that says how the size was counted. It replaces the `pascalgn/size-label-action`
workflow, with the same defaults, but runs in the GitHub App, so a repository needs no workflow and no token. The
design and its decision log are in [`../coverage_pr_size_labels_PRD.md`](../coverage_pr_size_labels_PRD.md) (#527).
The in-app guide for users is `/pr-size-labels/setup` (Guides in the sidebar).

**Status (2026-10-24):** built and tested (PRD M1–M10), not yet deployed. GitHub only: Bitbucket pull requests have no
labels (D8), and GitLab merge requests are out of scope (spike S4).

Paths below are relative to `apps/CodeCoverage/` unless they start with `docs/`.

## 1. Switching it on

The App needs **no new permission and no new webhook event** ([`hosting.md`](hosting.md#6-github-app)):
labels are written with *Issues: write* and *Pull requests: write*, the check run with *Checks: write*, and the trigger
is the `pull_request` event the App already receives.

| Where | Setting | Values |
|---|---|---|
| Account | **PR size labels** | on / off (default off) |
| Account | **Size scale** | rows of name, from-lines, colour, description |
| Account | **Ignored files** | rows of glob and mode (`Ignore`, `Ignore additions`) |
| Repository | **PR size labels** | `Use account setting` (default), `Label pull requests`, `Never label` |
| Repository | **Extra ignored files** | rows of glob and mode, applied after the account's |
| Repository | **Size labels in effect** | read-only: on or off, and whether the repository or the account decides |

The settings are private to the account's managers, like every other owner setting: a reader without the right sees
none of them, and a read-only member cannot change them (`OwnerPrivacyEndToEndTests`). On a Bitbucket account or
repository they are read-only, and the Account shows why (`PrSizeLabelsUnavailable`). A forge has the feature when an
`IForgeLabels` is registered for it.

## 2. How a size is counted

The size is the sum of the lines added and deleted in every file that counts (`PrSize/PrSizeCalculator.cs`). The
file list comes from GitHub with the pull request's own totals; a pull request with more than 3 000 files falls back to
those totals, as GitHub lists no more. Ignoring only lowers a size, so when the raw total is already below the smallest
row's ceiling (under 10 lines on the defaults) the files are not listed at all.

Each file is matched against the account's rules, then the repository's. **The last matching rule wins.** `Ignore`
drops the file; `Ignore additions` counts only its net deletions (deletions minus additions, never below 0). A glob that
starts with `!` counts the file in full again. A new account starts with the pascalgn defaults:

| Glob | Mode |
|---|---|
| `**/*.json` | Ignore additions |
| `**/*Tests/**` | Ignore additions |
| `**/package-lock.json` | Ignore |
| `**/packages.lock.json` | Ignore |
| `**/Directory.Packages.props` | Ignore |
| `**/*.md` | Ignore |

The scale maps the size onto a label: a pull request gets the row with the highest *from lines* it reaches
(`PrSize/PrSizeScale.cs`). The defaults (`PrSizeDefaults`, `CodeCoverage.Library/Entities/PrSizeLabel.cs`):

| Label | From lines | Colour |
|---|---|---|
| `size/XS` | 0 | `#009900` |
| `size/S` | 10 | `#77bb00` |
| `size/M` | 30 | `#eebb00` |
| `size/L` | 100 | `#ee9900` |
| `size/XL` | 500 | `#ee5500` |
| `size/XXL` | 1000 | `#ee0000` |

An empty scale means the defaults. Validation (`PrSize/PrSizeValidation.cs`) refuses duplicate names (case-insensitive)
or thresholds, a scale without a 0 row, and a description over 100 characters; a colour is stored as six hex digits.

## 3. The flow

```
pull_request opened / reopened / synchronize
  │  PullRequestUpdated (forge-webhooks.md), shared forge-event lane
  ▼
PrSizeLabelEventsRecipient     point-loads the repository and its account; feature off → nothing
  │  ApplyPrSizeLabelMessage
  ▼
PrSizeLabelRecipient           queue coverage-pr-size-labels, one writer per repository (D9)
  ├─ reconcile the label definitions when the scale changed (PrSizeLabelReconciler)
  ├─ count, pick the label, swap it in: removes only the app's other size label
  └─ "PR size" check run on the head commit, completed neutral
```

Settings changes go through the same queue (`PrSize/PrSizeLabelSwitch.cs`): a scale change publishes
`ReconcilePrSizeLabelsMessage` for every repository that has labels, and switching the feature off publishes
`RemovePrSizeLabelsMessage` when the owner chose Remove. The merge queue's label writer shares the queue, so two label
writes to one repository never race.

### The labels

The labels are the app's own. `Repository.PrSizeLabelState` remembers which GitHub label id belongs to which scale row,
so:

- **renaming a row renames the label**, and every pull request carrying it keeps it under the new name;
- a new colour or description is written to the existing label;
- **deleting a row deletes the label**, and GitHub removes it from every pull request, closed ones included;
- a label whose name matches a row when the labeller first runs is **adopted** (given the row's colour and
  description), so the labels an Action made are not duplicated;
- a label the user made, such as `size/foo`, is never touched;
- a label archived on GitHub is left archived; pull requests of that size get no label, and the check says why.

### The check run

One **PR size** check run per head commit, always completed **neutral**, so it never blocks a merge or the merge
queue. Its title is the label and the size (`size/M · 87 lines changed`); its summary lists the files that counted, the
ignored ones and those counted for their deletions only, each with the rule that matched, capped at 50 rows plus
"and N more" (GitHub limits the text to 65 535 characters). A retry updates the same run. A failure to label (a 403,
an archived label) still completes the run neutral, with the reason.

### Switching it off

Turning the labeller off, on the account or on a repository, answers with a prompt (HTTP 449, the retry action):
**Remove labels** saves and deletes the app's labels from every repository that stops labelling; **Keep labels** saves
and leaves them; **Cancel** saves nothing. On the account, Remove reaches only the repositories that inherit. An
unchanged save does not prompt.

## 4. Data

| Document | Field | Meaning |
|---|---|---|
| `Accounts/*` | `PrSizeLabels`, `PrSizeLabelScale`, `PrSizeIgnoreRules` | the account's switch and lists |
| `Repositories/*` | `PrSizeLabels` | `Inherit` when absent |
| `Repositories/*` | `PrSizeIgnoreRules` | the repository's extra rules |
| `Repositories/*` | `PrSizeLabelState` | scale row → GitHub label id; written only on the labeller's queue |

Migration `M_202610241200_BackfillPrSizeLabelDefaults` gives every existing account the default scale and ignore rules
once.

## 5. Troubleshooting

| Symptom | Cause |
|---|---|
| No label and no check run | The feature is off for the repository (see *Size labels in effect*), or the push came before it was switched on: the next push labels it. |
| Check run says the label is archived | Unarchive the label on GitHub, or rename the scale row. |
| Check run reports a 403 | The installation lacks *Issues: write* or *Pull requests: write*; accept the App's permission update. |
| Two size labels on a pull request | One of them is not the app's (another workflow still runs); remove that workflow. |
