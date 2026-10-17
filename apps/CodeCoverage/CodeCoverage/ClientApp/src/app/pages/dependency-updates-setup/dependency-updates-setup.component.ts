import { NgTemplateOutlet } from '@angular/common';
import { afterNextRender, ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { DEPENDENCIES_YML, MERGE_QUEUE_ON } from './dependency-updates-setup.snippets';

/** A step of the guide. The id is its anchor, kept stable so a message can link `/dependency-updates/setup#<id>`. */
interface SetupStep {
  id: string;
  title: string;
}

/**
 * The dependency updater's setup guide (/dependency-updates/setup): what the updater does, what it needs, and how to
 * switch it on and configure it. The reference behind it is docs/code-coverage/dependency-updates.md (the user guide)
 * and the decisions of docs/code-coverage/dependency-updates-PRD.md §13.10–§13.12 (R5, R11, R16, D-N4).
 *
 * Built like the merge queue's guide (/merge-queue/setup): static documentation, open to anonymous visitors, one card per
 * step with an anchor, and the page scrolls to a `#<step>` fragment itself because the router has no anchor scrolling
 * (app.config.ts).
 */
@Component({
  selector: 'app-dependency-updates-setup',
  imports: [
    NgTemplateOutlet, RouterLink, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent,
    BsCodeSnippetComponent, BsTableComponent,
  ],
  templateUrl: './dependency-updates-setup.component.html',
  styleUrl: './dependency-updates-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class DependencyUpdatesSetupComponent {
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  protected readonly primaryColor = Color.primary;
  protected readonly infoColor = Color.info;
  protected readonly dangerColor = Color.danger;

  protected readonly steps: SetupStep[] = [
    { id: 'prerequisites', title: 'Before you start' },
    { id: 'enable', title: 'Turn it on for the account or a repository' },
    { id: 'onboarding', title: 'Merge the onboarding pull request' },
    { id: 'coverage-yml', title: 'Configure it in coverage.yml' },
    { id: 'commands', title: 'Approvals, holds and comment commands' },
    { id: 'platforms', title: 'Platform SDKs and code migrations' },
    { id: 'security', title: 'The security model' },
    { id: 'troubleshooting', title: 'Troubleshooting' },
  ];

  /** What the GitHub App needs for dependency updates (PRD D-N4); onboarding checks every one of them. */
  protected readonly permissions = [
    { name: 'Contents', access: 'Read and write', use: 'Read coverage.yml and the manifests on the default branch; push the rolling branch and the plan ref.' },
    { name: 'Pull requests', access: 'Read and write', use: 'Open and update the onboarding pull request and the rolling pull request.' },
    { name: 'Checks', access: 'Read and write', use: 'Read the rolling pull request\'s checks: green, red, and which ones failed.' },
    { name: 'Issues', access: 'Read and write', use: 'Receive the approve, hold, unhold, bisect and refresh comments, and react to them.' },
    { name: 'Actions', access: 'Read and write', use: 'Dispatch the lookup, apply and migrate workflows, read their runs and download their artifacts; re-run failed checks.' },
    { name: 'Workflows', access: 'Read and write', use: 'Add the workflow templates in the onboarding pull request; update uses: refs and setup-* versions.' },
    { name: 'Metadata', access: 'Read-only', use: 'Read the repository and who has write access to it.' },
    { name: 'Dependabot alerts', access: 'Read-only', use: 'Start a security run when an alert is raised.' },
  ];

  /** The webhook events the updater listens to (PRD D-N4). */
  protected readonly events = ['Workflow run', 'Check run', 'Check suite', 'Issue comment', 'Pull request', 'Push', 'Release', 'Dependabot alert'];

  /** The CodeCoverage settings (dependency-updates.md §3). */
  protected readonly settings = [
    { name: 'Dependency updates on', where: 'Account, overridable per repository', defaultValue: 'off' },
    { name: 'Onboarding adds the merge queue (only matters when the onboarding pull request is made)', where: 'Account, overridable per repository', defaultValue: 'yes' },
    { name: 'Schedule (frequency, weekday, hour)', where: 'Account (in its time zone), overridable per repository', defaultValue: 'weekly, Monday, 06:00' },
    { name: 'Time zone', where: 'Account', defaultValue: 'UTC' },
    { name: 'Cooldown (how old a release must be)', where: 'Account, overridable per repository', defaultValue: '3 days' },
    { name: 'Bisect budget (CI runs spent finding a culprit)', where: 'Account, overridable per repository', defaultValue: '2' },
    { name: 'Account rules (match, max update, ignore, group, enforced)', where: 'Account', defaultValue: 'none' },
  ];

  /** The `dependencies:` keys (dependency-updates.md §4, parsed by DependenciesYml.cs). */
  protected readonly keys = [
    { key: 'enabled', defaultValue: 'the setting', meaning: 'false opts this repository out. Opting in is the CodeCoverage setting, never the file.' },
    { key: 'schedule', defaultValue: 'the setting', meaning: 'frequency (daily, weekly, monthly), weekday and hour of the slot.' },
    { key: 'cooldown', defaultValue: 'the setting', meaning: 'How old a release must be before it is planned. It may only lengthen the account\'s cooldown.' },
    { key: 'autoMerge.default', defaultValue: 'minor', meaning: 'never, patch, minor or major: the largest update planned without an approval, which is also the largest that may auto-merge.' },
    { key: 'autoMerge."<glob>"', defaultValue: '"@angular/*": never, "actions/*": minor', meaning: 'A threshold for the matching dependencies; the most specific glob wins.' },
    { key: 'autoMerge.maxCoverageDrop', defaultValue: '0', meaning: 'Percentage points the coverage may drop before auto-merge is refused.' },
    { key: 'ignore', defaultValue: 'none', meaning: 'Dependency name globs never updated.' },
    { key: 'rules[].match', defaultValue: '—', meaning: 'A glob or a list of globs over the dependency name.' },
    { key: 'rules[].ecosystem', defaultValue: 'every ecosystem', meaning: 'Only dependencies of this ecosystem (npm, nuget, github-actions, …).' },
    { key: 'rules[].group', defaultValue: 'none', meaning: 'The matching dependencies move together, in one group.' },
    { key: 'rules[].allowMajor', defaultValue: 'false', meaning: 'Plan majors without an approval. They still never auto-merge.' },
    { key: 'rules[].hold', defaultValue: 'none', meaning: 'true or a reason: held, and listed as held, until removed.' },
    { key: 'rules[].ignore', defaultValue: 'false', meaning: 'Never update the matching dependencies.' },
    { key: 'exclude', defaultValue: 'none', meaning: 'Path globs of package files left alone.' },
    { key: 'lookupAction', defaultValue: 'dependency-lookup.yml', meaning: 'The lookup workflow\'s file name under .github/workflows.' },
    { key: 'applyAction', defaultValue: 'dependency-apply.yml', meaning: 'The apply workflow\'s file name under .github/workflows.' },
    { key: 'migrate[]', defaultValue: 'none', meaning: 'match plus onMajor: the workflow that runs code migrations after an approved major.' },
    { key: 'platforms.<sdk>', defaultValue: 'auto-detected', meaning: 'dotnet, node, python, java or go: strategy (replace or add, default replace), keepPrevious (0), lockPackageMajor (false), locations and exclude.' },
    { key: 'lockFileMaintenance', defaultValue: 'false', meaning: 'Also refresh transitive dependencies in the lockfiles.' },
    { key: 'registries', defaultValue: 'none', meaning: 'Lockfile hosts allowed besides the public registries, e.g. npm.pkg.github.com.' },
  ];

  /** The comment commands (dependency-updates.md §5). */
  protected readonly commands = [
    { command: 'approve <group>', does: 'Takes an over-threshold group, such as a major, into the pull request.' },
    { command: 'hold <group or package>', does: 'Holds it until unhold.' },
    { command: 'unhold <group or package>', does: 'Lifts a comment or human hold. The bot\'s own holds lift by themselves.' },
    { command: 'bisect', does: 'Bisects a red pull request now (a budget of 4 when the setting is off).' },
    { command: 'refresh', does: 'Rebuilds now. A run a comment started never auto-merges; the next scheduled one may.' },
  ];

  /** The last run's outcomes (dependency-updates.md §9). */
  protected readonly outcomes = [
    { outcome: 'workflow-missing', meaning: 'The lookup or apply workflow is not on the default branch.', action: 'Merge the onboarding pull request.' },
    { outcome: 'workflow-differs, workflow-unsafe', meaning: 'The dispatched workflow is not the default branch\'s, or it may write.', action: 'Restore the template\'s permissions.' },
    { outcome: 'dispatch-uncorrelated, *-timeout, *-missing', meaning: 'A run never answered ("silent").', action: 'Check the Actions tab; the next slot retries.' },
    { outcome: 'lookup-failed, apply-failed', meaning: 'The workflow run failed.', action: 'Open the run from the history.' },
    { outcome: 'patch-rejected', meaning: 'The apply patch changed something the plan did not allow.', action: 'Read the reason in the comment; it is a bug or an attack, never ignored.' },
    { outcome: 'replay-conflict, rolling-rewritten', meaning: 'The rolling branch needs a person ("frozen").', action: 'Resolve on the branch, or close the pull request.' },
    { outcome: 'red', meaning: 'CI failed and no culprit was found within the budget.', action: 'hold the suspect, or bisect.' },
    { outcome: 'nothing-planned', meaning: 'Everything is current, held, cooling down or waiting for an approval.', action: 'See the pull request body.' },
    { outcome: 'onboarding blocked', meaning: 'A permission or event is missing.', action: 'Accept it on GitHub; the notice links there.' },
  ];

  protected readonly snippets = {
    dependencies: DEPENDENCIES_YML,
    mergeQueueOn: MERGE_QUEUE_ON,
  };

  constructor() {
    afterNextRender(() => {
      const fragment = this.route.snapshot.fragment;
      if (fragment) this.jumpTo(fragment);
    });
  }

  protected jumpTo(id: string): void {
    this.document.getElementById(id)?.scrollIntoView({ block: 'start' });
  }
}
