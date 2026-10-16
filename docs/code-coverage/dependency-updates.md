# Dependency updates

CodeCoverage keeps a repository's dependencies current with **one rolling pull request**, `mintplayer/dependencies`,
rebuilt on a schedule. It updates any ecosystem the lookup reports (npm, pnpm, yarn, NuGet, Maven, Gradle, Cargo, Go,
Python, Composer, Bundler, Docker, GitHub Actions, …) and the platform SDKs (.NET, Node.js, Python, Java, Go). Nothing of
your repository ever runs on the CodeCoverage server: every restore, relock and migration runs in **your own GitHub
Actions**, in workflows the bot dispatches and whose results it checks before anything is pushed.

The design, its decisions and their evidence are in [dependency-updates-PRD.md](dependency-updates-PRD.md) (§13 is
authoritative). This page is the user guide.

## 1. How it works, in one run

1. **Lookup.** At the repository's slot the bot dispatches `dependency-lookup.yml` on the default branch. It runs
   Renovate in lookup-only mode (pinned by digest, read-only token, no push) and uploads `lookup.json`: every dependency,
   its current value and the versions available.
2. **Plan.** The bot applies your policy (thresholds, holds, cooldown, account rules, fleet holds, approvals) and commits
   the plan as one file, `.mintplayer/dependency-plan.json`, on the bot-only ref `mintplayer/dependencies-work`.
3. **Apply.** It dispatches `dependency-apply.yml` on that ref. The workflow edits the manifests, pins and relocks with
   scripts off, never builds, and uploads `dependency.patch` and `result.json`. A group the resolver refuses is reset and
   held; the others still ship.
4. **Validate and push.** The bot's git writer checks the patch against the plan line by line (only planned files, only
   the planned version tokens, lockfiles from known registries, no added files, no scripts) and builds the rolling branch
   from the default branch. Commits people pushed on the rolling branch are replayed on top.
5. **Pull request and CI.** The pull request is opened or updated, with what was applied, what waits for an approval,
   what is held and why. When CI is green and every group is within its auto-merge threshold, the pull request is handed
   to the [merge queue](merge-queue.md). When CI is red, the bot bisects to find the group that broke it.

A slot whose rebuilt tree equals the pushed one pushes nothing and starts no CI.

## 2. Onboarding

1. **Turn it on** for the account (the Account page, *Dependency updates*), or for one repository (the Repository page,
   which can also turn it off for itself).
2. **The onboarding pull request.** Within a day the bot opens `mintplayer/dependencies-onboarding`. It adds:
   - `.github/workflows/dependency-lookup.yml` and `dependency-apply.yml`, thin callers of the published action, pinned
     to a commit (the tag kept as a comment);
   - `angular-migrate.yml` / `nx-migrate.yml` when the repository has `angular.json` / `nx.json`;
   - a commented `dependencies:` section in `coverage.yml`;
   - and it removes the version-update entries of `.github/dependabot.yml`, translating its `ignore:` into rules.
     Dependabot **alerts** stay on: they feed the security trigger (§6).
3. **Merge it.** The first rolling pull request after onboarding is a **draft** and never auto-merges: review it once.

**Permissions.** Before it opens the onboarding pull request, the bot checks that the App requests, and the installation
has accepted, what dependency updates need: *Contents*, *Pull requests*, *Checks*, *Issues*, *Actions* and *Workflows*
(read & write), *Dependabot alerts* (read), and the `workflow_run`, `check_run`, `check_suite`, `issue_comment`,
`pull_request`, `push`, `release` and `dependabot_alert` events. A gap blocks onboarding with the reason, shown on the
repository's *Dependency updates* panel and the account's fleet view; the Account page's permission notice lists it too,
with the link to accept. Onboarding is retried daily. See [hosting.md §6](hosting.md#6-github-app).

## 3. Settings

| Setting | Where | Default |
|---|---|---|
| Dependency updates on | Account, overridable per repository | off |
| Schedule (frequency, weekday, hour) | Account (with its time zone), overridable per repository | weekly, Monday, 06:00 |
| Time zone | Account | UTC |
| Cooldown (how old a release must be) | Account, overridable per repository | 3 days |
| Bisect budget (CI runs spent finding a culprit) | Account, overridable per repository | 2 |
| Account rules (match, max update, ignore, group, enforced) | Account | none |

Runs within a slot hour are spread by repository, so a fleet does not dispatch at once. An **enforced** account rule is a
ceiling no file and no approval can raise; a non-enforced one applies only where the file says nothing.

## 4. `coverage.yml`: `dependencies:`

Read from the **default branch only**; a change on the rolling branch has no effect. A section with an error falls back
to the defaults (keeping only a valid `enabled: false`), and the error is listed in the pull request.

```yaml
dependencies:
  enabled: false                 # opt this repository out (opting in is the setting)
  schedule: { frequency: weekly, weekday: monday, hour: 6 }   # daily | weekly | monthly
  cooldown: 7d                   # may only lengthen the account's cooldown
  autoMerge:
    default: minor               # never | patch | minor | major   (built-in: minor)
    "@angular/*": never          # the most specific glob wins
    maxCoverageDrop: 0.5         # percentage points; built-in 0
  ignore: ["Microsoft.CodeAnalysis*"]
  rules:
    - match: ["vitest", "@vitest/*"]
      ecosystem: npm             # optional
      group: vitest              # move together
      allowMajor: true           # plan majors without an approval (they still never auto-merge)
      hold: "waiting for the Nx plugin"   # or true; held until removed
  exclude: ["samples/**"]        # package files left alone
  lookupAction: dependency-lookup.yml
  applyAction: dependency-apply.yml
  migrate:
    - match: "@angular/core"
      onMajor: angular-migrate.yml
  platforms:
    dotnet: { strategy: replace, keepPrevious: 0, lockPackageMajor: false, locations: [], exclude: [] }
  lockFileMaintenance: false     # transitive-only refreshes, off by default
  registries: ["npm.pkg.github.com"]   # lockfile hosts allowed besides the public registries
```

**Thresholds.** The threshold is both "planned without asking" and "may auto-merge". Built in: `default: minor`,
`@angular/*: never`, `actions/*: minor`; GitHub Actions and platforms never go above minor without an approval. Every
group of a library family (`@angular/*`, `nx` + `@nx/*`, …) moves together.

## 5. Approvals and commands

Writers (write access or above) comment on the rolling pull request, one command per comment:

| Command | Does |
|---|---|
| `approve <group>` | Takes an over-threshold group (a major) into the pull request. Bound to the exact dependency set, the target major and the `coverage.yml` it was given under: a newer major, a changed group or another `coverage.yml` expires it. Checked again at apply time: an approver who lost write access loses the approval. |
| `hold <group or package>` | Holds it until `unhold`. |
| `unhold <group or package>` | Lifts a comment or human hold (the bot's own holds lift by themselves). |
| `bisect` | Bisects a red pull request now (budget 4 when the setting is off). |
| `refresh` | Rebuilds now. A run a comment started never auto-merges; the next scheduled one may. |

Edited comments and comments by bots are ignored. The bot reacts 👍 or 👎.

## 6. What happens when…

- **A release is very new.** It waits out the cooldown (the release time from the registry; for a record without one,
  the time the bot first saw it). The account's own releases (published by one of its repositories, matched to a release
  or push event of that repository) skip the cooldown and propagate to the account's consumers in graph order.
- **A vulnerability is announced.** A Dependabot alert starts an out-of-slot security run with only the fix and the
  groups that may auto-merge anyway. A major fix still waits for `approve`.
- **The resolver refuses a group** (e.g. Angular N+1 before the Nx plugin supports it). The group is reset and held,
  naming the blockers; the hold lifts when a newer version of a blocker appears, and after four slots in any case.
- **CI is red.** The failing checks are re-run once (flaky tests). Then suspects are left out one by one (majors first,
  then versions red elsewhere in the fleet), one CI run each, until the culprit is found; it is held, recorded as red for
  the whole account, and the rest is rebuilt without it. The comment says whether a *build* or a *test* check failed,
  read from the failing checks' names; logs are not read.
- **You push a commit on the rolling branch.**
  - A manifest value you set wins, and becomes a hold, so the next slot does not undo it.
  - A code fix (any file that is not a dependency file) keeps the versions it was written against: those groups stay
    pinned, and the pull request lists them under *Pinned for a fix on this branch*, until it is merged or closed. Such a
    pull request is merged by a person.
  - A lockfile you changed conflicts with the rebuild: the bot takes its own side and regenerates the lockfile through
    your apply workflow, on top of your commits, before anything is pushed. A lockfile whose every planned dependency you
    changed by hand cannot be regenerated that way; the run then stops once, retries at once without the dependencies you
    set, and stops for a person if it conflicts again.
  - Any other conflict, or a branch rewritten by hand: the branch is kept as it is and the bot comments; close the pull
    request to let it start over.
- **A major needs code migrations** (`migrate:`). After `approve`, the migrate workflow runs `ng update --migrate-only` /
  `nx migrate --run-migrations` with no secrets. A migration that does not complete drops its group. A pull request with
  migrations never auto-merges and says so.
- **A platform SDK moves** (`net10.0` → `net11.0`, Node 22 → 24). Every location moves together (TFM, `global.json`,
  Dockerfile `FROM`, `setup-*`); a computed TFM is reported, never guessed. Previews are followed only when the repository
  is already on one. A platform major never auto-merges.

## 7. Seeing the state

- **Repository page → Dependency updates:** the rolling pull request, the last run and how it ended, the next slot, the
  holds and approvals waiting, the pinned versions, what the pull request applies, and the recent steps.
- **Account page → Dependency updates:** per repository (state: CI red, frozen, a run that never answered), per package
  (which repository is on which version, and what the bot does with it there), and **platform readiness**: "Angular 23:
  3 of 5 ready", with the blockers. *Ready* means the lookup offers the next major there and only an approval, the
  cooldown or an account rule stands in the way; *blocked* means the resolver refused it, or it went red in the fleet;
  *not offered yet* means no lookup has offered it. The move itself is an `approve` or a rule change.
- Every row is filtered like the account's repository list: a private repository you cannot see appears nowhere.
- The **dependency graph** card reads the lookup's packages where a lookup ran (every ecosystem it reports, with
  versions), and the manifest scanners elsewhere.

## 8. Security model

- **No repository code runs on the server.** The server runs `git` only (no hooks, no filters, no merge drivers).
- **The workflows are read-only.** The shipped templates have `contents: read`, no `id-token`, `persist-credentials:
  false`; the bot refuses to dispatch a workflow (yours included) with write permissions, `id-token`, a missing top-level
  `permissions:` or a `pull_request_target` trigger, and a migrate workflow that reads secrets. The dispatched workflow
  file must equal the default branch's.
- **Feed secrets** reach only the lookup and the apply of ecosystems whose relock runs no package code (npm, pnpm, yarn,
  NuGet restore, Composer with `--no-plugins`, Cargo, Go, Maven); never Gradle; uv only with `--no-build`.
- **Every patch is judged by the bot, not by the runner**: only planned package files that exist, each changed line a
  planned `current → new` swap on a line naming the dependency, no added or deleted files, no `Directory.Build.*`,
  `.npmrc`, `NuGet.Config`, install scripts or `.github/` (except `uses:` refs and `setup-*` versions, validated against
  the target `action.yml`), lockfile URLs on known registries only, integrity on every entry, and more than 25 new
  packages needs a person. The plan file never reaches the rolling branch.
- **Pushes** use a token minted per job for the one repository, revoked afterwards.

**Residual risk, stated plainly.** The validators stop the runner from changing anything beyond the plan; they cannot make
a *planned* package safe. Once a malicious version is planned, your pull request's CI installs it **with your CI's
secrets** (npm scripts in CI's own install, NuGet `build/*.targets` on build). The cooldown, the fleet holds and a person's
review of majors are what reduce that risk.

## 9. Troubleshooting

The last run's outcome is on the repository panel; the full story is in *Recent steps* and on the pull request.

| Outcome | Meaning | What to do |
|---|---|---|
| `workflow-missing` | The lookup or apply workflow is not on the default branch. | Merge the onboarding pull request. |
| `workflow-differs`, `workflow-unsafe` | The dispatched workflow is not the default branch's, or it may write. | Restore the template's permissions. |
| `dispatch-uncorrelated`, `*-timeout`, `*-missing` | A run never answered ("silent"). | Check the Actions tab; the next slot retries. |
| `lookup-failed`, `apply-failed` | The workflow run failed. | Open the run from the history. |
| `patch-rejected` | The apply patch changed something the plan did not allow. | Read the reason in the comment; it is a bug or an attack, never ignored. |
| `replay-conflict`, `rolling-rewritten` | The rolling branch needs a person ("frozen"). | Resolve on the branch, or close the pull request. |
| `red` | CI failed and no culprit was found within the budget. | `hold` the suspect, or `bisect`. |
| `nothing-planned` | Everything is current, held, cooling down or waiting for approval. | See the pull request body. |
| onboarding blocked | A permission or event is missing. | Accept it on GitHub (the notice links there). |
