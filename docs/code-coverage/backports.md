# Backports

How the app carries a merged change onto older release lines. Versioning itself (tags, releases, `lines:`) is in
[versioning.md](versioning.md), and the queue that merges backports is in [merge-queue.md](merge-queue.md).
The in-app guide for users is `/backports/setup` (Guides in the sidebar).

**Who may ask.** A `/backport` comment, or a `backport:` label added to a merged pull request, needs **write access**
to the repository. Anything less gets a comment saying so, and nothing runs. Labels already on a pull request when it
merges are backported on the merger's behalf: the merge itself needed write access. A `backport/<pr>-<line>` branch
you push yourself opens its pull request only when you have write access. With release lines, "Set up rulesets" adds
a ruleset on `backport/*` that only the App and repository admins pass (merge-queue.md §3, ruleset 4): resolving a
conflicted backport by hand is then an admin's job, and in return the App's backports merge without an approval.
A pull request of your own into a release line passes the required "Line policy" check only on a **past** line; the
current line of each ecosystem takes only the App's backports, so a fix goes to the default branch first
(merge-queue.md §3, ruleset 3).
Edited comments and bots are never commands.

**What you get back.** One comment on the original pull request, with a line per release line: the backport pull
request (and whether it went to the merge queue), "already holds this change", a conflict with its files and the
commands for a manual backport, or a rejected push. Conflicts the rules resolved, platform values the change edits on
the line, and first-party packages the line keeps at its own major are listed underneath.

**Where every backport ends up.** The repository page's **Backport attempts** list has one row per backported pull
request and line. Each row shows the outcome (opened, draft with conflicts, regenerating, already on the line, branch
exists, conflicts or failed), the backport pull request and the reason. A retry replaces the row. A backport the
forge refused before any comment could be posted (a missing App permission, a forge error) still gets its row,
under the line's name as it was asked for. A comment can be missed; the list is where a lost backport shows up.

**A backport the merge queue holds.** On a queued line, a backport that fails its checks or conflicts holds the line
until it is fixed ([merge queue, release lines](merge-queue.md)). Its row turns **held**, it gets the `backport: needs
attention` label, and its source pull request's author (for a fork's or a bot's, the CODEOWNERS of its files) is
assigned and reminded after 2 working days. The **Held backports** page in the app lists every held backport that
waits for you.

## Release branches

Branches matching `releaseBranches` are release lines. They are versioned like the default branch, within the major
their own content computes. `master` on `net10.0` and `release/8.x` on `net8.0` release `10.2.3` and `8.1.2` side
by side.

**Auto-cut:** when a merge into the default branch moves a platform major, for example from `net10.0` to
`net11.0`, the app creates `release/10.x` at the merge's first parent and comments on the pull request. Free
packages never cut a branch.

**Backport:** label a pull request `backport: 8.x`, or comment `/backport 8.x`. Once it is merged, the app
dry-runs the cherry-pick onto every requested line. Each line is handled on its own:

- **Platform values are normalized first.** In the change and its parent, the app rewrites the target framework, the
  framework package versions (`Microsoft.AspNetCore.*`, `.Extensions.*`, `.EntityFrameworkCore.*`), the
  `global.json` SDK, the `dotnet-version`/`node-version` in `.github` workflows, and the `@angular/*`, `react`,
  `react-dom` and `vue` ranges in `package.json` to the line's own values. A hunk that only moved the platform then
  vanishes, and an edit next to one merges.
- **A change to the line's own platform is never normalized away.** For example, `net11.0` → `net10.0;net11.0` on
  a .NET 10 line. It merges or conflicts as git decides, and the comment names it.
- **Conflicts the merge queue may resolve are resolved.** These are conflicts matching the default branch's
  `mergeQueue.autoResolve` rules (never a line's own copy), with the line's side as the base. The pull request lists
  each one.
- **A line that applies** gets `backport/<pr>-<line>` and its own pull request. The CI run on that pull request is
  the compile check against the old target. A line that already holds the change gets nothing.
- **A line that conflicts** gets nothing pushed. The comment names its files and gives the commands for a manual
  backport. The other lines are not held back.
- **Lockfiles and other regenerated files are never merged.** A file that matches `mergeQueue.regeneratedPaths` keeps
  the line's version. The line's own regenerate workflow then runs on the backport branch:
  `postRebaseActionName`, or the `action` of the rule that resolved the file. The workflow file must be the same as at
  the line's tip. Its `merge-queue-patch` is folded into the backport commit, and only then does the pull request open.
  If the regeneration cannot run or fails, the pull request opens anyway, with a **Not regenerated** note listing the
  files to regenerate by hand. A line without the workflow at all gets it from onboarding: when **Set up features**
  with *Release lines* merges, each supported line missing a regenerate workflow that the default branch's
  `mergeQueue` names gets a pull request adding it (#571 D9, [onboarding.md](onboarding.md#on-merge)).

**Onboarding a repository with lines.** **Set up features** → *Release lines* adds `versioning.lines`, and when its
pull request merges it renames old line branches (such as `release/6.0`) to `release/<ecosystem>-<major>.x` through
GitHub's rename API (#571 D2). GitHub moves their open pull requests to the new name. `git fetch` does not follow
the old name, so local clones switch to the new branch themselves. See [onboarding.md](onboarding.md).

A `backport:` section in the default branch's `coverage.yml` adjusts this:

```yaml
backport:
  onConflict: draft          # comment (default) | draft
  enqueue: false             # default true: hand each backport pull request to the merge queue
  defaultLabels: fixes       # all (default) | fixes | off
  fixLabels: [bug]           # what marks a fix under `fixes`, beside a fix: title
  trialPick: warn            # warn (default) | refuse | off: try the backports before the merge queue merges
  trialPicks:
    dotnet-11.x: refuse      # the same, per line
  lines:
    dotnet-8.x: keep         # supported past its platform's end of life
    angular-20.x: retire     # no longer supported, whatever the lifecycle says
  markers:
    disable: [setup-action]  # tfm | framework-packages | global.json | setup-action | npm-frameworks
    packages: ["@mintplayer/ng-bootstrap", "MyCompany.*"]
```

- **`onConflict: draft`** pushes a conflicting line anyway, with git's conflict markers committed, and opens a
  **draft** pull request that lists the files. Resolve them on that branch, then mark the pull request ready. The
  `autoResolve` rules are not applied to a draft. To get a draft for one request only, comment
  `/backport <line> --draft`; a conflict comment names that command for each conflicting line.
- **`enqueue`** (default `true`): a backport pull request goes to the merge queue when the queue is enabled and
  `mergeQueue.branches` names its line, for instance through `supported-lines` ([merge-queue.md](merge-queue.md)).
  The queue's approvals and checks still apply. A draft, or a backport whose regenerated files are stale, is never
  enqueued.
- **`defaultLabels`**: with release lines on, a pull request into the default branch gets a `backport: <line>` label,
  when it opens, for each supported line whose ecosystem it changes (the same changed-files test as a release). Remove
  the labels of the lines it should not reach; merging backports it to the rest. `fixes` labels the older lines only
  for a `fix:` title or a `fixLabels` label, and the newest line always; `off` labels nothing. Every pull request
  also gets an informational **Backport targets** check listing the lines and which apply. A pull request a bot opened
  gets no labels: the dependency updater gives each supported line a run of its own
  ([dependency-updates.md](dependency-updates.md) §4, `dependencies.lines`).
- **`trialPick`** (default `warn`): when the merge queue is about to merge a pull request into the default branch, it
  first tries the backport onto each line the pull request is labelled for, exactly as the backport will merge it, and
  pushes nothing. It runs while the queued commit's checks run, so a label can still be removed. Under `warn`, a line that
  would conflict gets one comment with its files, and the merge goes on. Under `refuse`, the queue removes the pull
  request instead. `off` tries nothing. `trialPicks` sets the mode of one line. A current line that is promoted
  (`/release`) is never tried, since it takes no backport. A trial that cannot run never holds a merge back: the backport
  after the merge reports the conflict as it always did.
- **`lines`** overrides which lines are supported. By default a line is supported while its platform major has not
  reached end of life (the lifecycle data this app collects), and the newest line of each ecosystem always is. An
  ecosystem without lifecycle data supports only its newest line and the lines you `keep`. `retire` wins over
  everything, the newest line included.
- **`markers.disable`** leaves a built-in kind of platform value to the plain merge.
- **`markers.packages`** adds your own platform-anchored packages, NuGet or npm, with `*` as a wildcard. A first-party
  package whose major follows the platform, such as a component library tied to Angular, is the typical case.
  When a change raises such a package above a line's major, the line keeps its own version, and the backport comment
  says the line probably needs what the newer version brought. If that version was released by a public repository on
  this server, or by this one, the comment names the release and shows which of its pull requests that repository has
  already backported to a line of the same major. The app never starts a backport in another repository.

A section with errors is named in the backport comment, and its defaults apply.

**Where a change was backported.** When a backport merges into a line, its original pull request gets a
`backported: <line>` label. The app's own backports are recognised by a hidden marker in their description. A backport
you make by hand is recognised too, when its description says `Backport of #N`, its branch is `backport/<N>-<line>`, or
its commits were picked with `git cherry-pick -x`; the original then also gets a comment linking it. If you create a
`backport/<N>-<line>` branch yourself (the commands a conflict comment gives you), the app opens its pull request into
the line once you push it. The original must be a pull request merged into the default branch.

> **Protect release branches yourself.** A branch the app creates is unprotected unless a ruleset matches it. Add a
> repository ruleset targeting `release/*` with at least *Restrict deletions* and *Block force pushes*, and require
> your CI checks if you merge backports through it.

