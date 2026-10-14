import { NgTemplateOutlet } from '@angular/common';
import { afterNextRender, ChangeDetectionStrategy, Component, computed, DOCUMENT, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { AccountPermissionsNoticeComponent } from '../../components/account-permissions-notice/account-permissions-notice.component';
import { MERGE_QUEUE_SETUP_SCREENSHOTS, type MergeQueueSetupScreenshot } from './merge-queue-setup.screenshots';
import { COVERAGE_YML, CREATE_LABEL, CREATE_RULESETS, PUSH_TRIGGER, REGENERATE_WORKFLOW, RULESET_1, RULESET_2 } from './merge-queue-setup.snippets';

/** A step of the guide. The id is its anchor, kept stable so a message can link `/merge-queue/setup#<id>`. */
interface SetupStep {
  id: string;
  title: string;
}

/**
 * A screenshot slot. Its URL, a GitHub attachment, is in MERGE_QUEUE_SETUP_SCREENSHOTS, whose file also says what each
 * screenshot shows and how it is captured. Every capture is 1280×720, the size the template declares.
 */
interface Screenshot {
  name: MergeQueueSetupScreenshot;
  alt: string;
  caption: string;
}

/**
 * "Optimal GitHub setup" (/merge-queue/setup): how to set a GitHub repository up so that every merge goes through the
 * CodeCoverage merge queue and the queue is held to exactly the reviews and checks everyone else is (merge-queue PRD
 * O13). The reference behind it is docs/code-coverage/merge-queue.md §2–3.
 *
 * Static documentation, open to anonymous visitors like /connect-agent. Opened as `?provider=github&account=<login>`
 * (the merge-queue panel of a repository page and the account page's permissions notice link it that way), step 1
 * also shows that account's live "accept the new permissions" notice (O17). The notice hides itself from anyone who
 * does not manage the account.
 *
 * The router has no anchor scrolling (app.config.ts), so the page scrolls to a `#<step>` fragment itself.
 *
 * Screenshots are GitHub attachments (O23) and use native `loading="lazy"` with explicit `width`/`height` (O13). One
 * without a URL yet is a "Screenshot pending" box of the same size, and nothing is requested for it; one that fails to
 * load becomes the same box, never a broken image.
 */
@Component({
  selector: 'app-merge-queue-setup',
  imports: [
    NgTemplateOutlet, RouterLink, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent,
    BsCodeSnippetComponent, BsTableComponent, AccountPermissionsNoticeComponent,
  ],
  templateUrl: './merge-queue-setup.component.html',
  styleUrl: './merge-queue-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class MergeQueueSetupComponent {
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  protected readonly infoColor = Color.info;
  protected readonly dangerColor = Color.danger;

  protected readonly steps: SetupStep[] = [
    { id: 'permissions', title: 'Install the App and accept its permissions' },
    { id: 'native-queue', title: 'Turn off GitHub\'s merge queue, allow squash merging' },
    { id: 'ruleset-1', title: 'Ruleset 1: only the merge queue updates the branch' },
    { id: 'ruleset-2', title: 'Ruleset 2: reviews and checks' },
    { id: 'coverage-yml', title: 'coverage.yml on the default branch' },
    { id: 'regenerate', title: 'The regenerate workflow' },
    { id: 'forks', title: 'Pull requests from forks' },
    { id: 'settings', title: 'Organization settings in CodeCoverage' },
  ];

  /** What the queue needs from the GitHub App (merge-queue.md §2.1); the first three are new with the queue. */
  protected readonly permissions = [
    { name: 'Actions', access: 'Read and write', isNew: true, use: 'Dispatch the regenerate workflow, read its run, download its patch, re-run a failed job.' },
    { name: 'Workflows', access: 'Read and write', isNew: true, use: 'Push a rebased branch that brings in the base\'s own workflow changes.' },
    { name: 'Commit statuses', access: 'Read-only', isNew: true, use: 'Wait for commit statuses as well as check runs.' },
    { name: 'Contents', access: 'Read and write', isNew: false, use: 'Push the rebased and squashed commits, merge, read coverage.yml.' },
    { name: 'Pull requests', access: 'Read and write', isNew: false, use: 'Read the pull request and its reviews; the label and the queue\'s comments.' },
    { name: 'Checks', access: 'Read and write', isNew: false, use: 'Publish the "Merge queue" check run; read every other check.' },
    { name: 'Metadata', access: 'Read-only', isNew: false, use: 'Read repository roles and rulesets.' },
    { name: 'Issues', access: 'Read-only', isNew: false, use: 'Receive the /queue and /dequeue comments.' },
  ];

  /** The `mergeQueue:` keys (merge-queue.md §2.2, parsed by MergeQueueYml.cs). */
  protected readonly keys = [
    { key: 'enabled', defaultValue: 'false', meaning: 'The only on/off switch; there is none in CodeCoverage.' },
    { key: 'branches', defaultValue: 'the default branch', meaning: 'The base branches the queue serves.' },
    { key: 'mergeMethod', defaultValue: 'squash', meaning: 'squash is the only method.' },
    { key: 'enqueue.label', defaultValue: 'merge-queue', meaning: 'The label that queues a pull request.' },
    { key: 'postRebaseActionName', defaultValue: 'none', meaning: 'The regenerate workflow\'s file name under .github/workflows (step 6).' },
    { key: 'postRebaseTimeout', defaultValue: '20m', meaning: 'How long the regenerate run may take: 1 minute to 6 hours.' },
    { key: 'postRebaseWhen', defaultValue: 'auto-resolved', meaning: 'When to run it: auto-resolved (after a resolved conflict), always or never.' },
    { key: 'regeneratedPaths', defaultValue: 'none', meaning: 'The only paths the regenerate patch may touch. List at least one.' },
    { key: 'autoResolve[].path', defaultValue: '—', meaning: 'A wholly generated file whose conflicts take the base side; the regenerate run rewrites it.' },
    { key: 'autoResolve[].strategy', defaultValue: 'take-base', meaning: 'take-base is the only strategy.' },
    { key: 'autoResolve[].keys', defaultValue: 'none', meaning: 'Optional: only conflicts on these top-level JSON members resolve.' },
    { key: 'checks.retryFlaky', defaultValue: '1', meaning: 'Re-runs of a failed check before the pull request leaves the queue: 0 to 3.' },
    { key: 'checks.timeout', defaultValue: '60m', meaning: 'How long the queue waits for checks: 5 minutes to 12 hours.' },
  ];

  protected readonly snippets = {
    coverageYml: COVERAGE_YML,
    createLabel: CREATE_LABEL,
    ruleset1: RULESET_1,
    ruleset2: RULESET_2,
    createRulesets: CREATE_RULESETS,
    regenerate: REGENERATE_WORKFLOW,
    pushTrigger: PUSH_TRIGGER,
  };

  protected readonly shots = {
    acceptPermissions: {
      name: 'accept-permissions',
      alt: 'GitHub asking an organization owner to accept the CodeCoverage App\'s new permissions: Actions and Workflows read and write, Commit statuses read-only',
      caption: 'GitHub: the installation\'s request for the new permissions.',
    },
    nativeQueueOff: {
      name: 'native-merge-queue-off',
      alt: 'A branch ruleset\'s rules on GitHub with Require merge queue unticked',
      caption: 'Settings → Rules → Rulesets: Require merge queue stays off.',
    },
    squashMerging: {
      name: 'squash-merging',
      alt: 'The Pull Requests section of a repository\'s general settings on GitHub with Allow squash merging ticked',
      caption: 'Settings → General → Pull Requests: Allow squash merging.',
    },
    ruleset1Bypass: {
      name: 'ruleset-1-bypass',
      alt: 'Ruleset 1\'s bypass list on GitHub: the CodeCoverage App set to Exempt and the Repository admin role set to For pull requests only',
      caption: 'Ruleset 1: the bypass list.',
    },
    ruleset1Rules: {
      name: 'ruleset-1-rules',
      alt: 'Ruleset 1 on GitHub targeting the default branch, with Restrict updates as its only branch rule',
      caption: 'Ruleset 1: target branches and rules.',
    },
    ruleset2PullRequest: {
      name: 'ruleset-2-pull-request',
      alt: 'Ruleset 2\'s Require a pull request before merging rule on GitHub, with Dismiss stale pull request approvals and Require approval of the most recent reviewable push unticked',
      caption: 'Ruleset 2: required reviews, both approval settings off.',
    },
    ruleset2StatusChecks: {
      name: 'ruleset-2-status-checks',
      alt: 'Ruleset 2\'s Require status checks to pass rule on GitHub, listing the repository\'s CI checks and not the Merge queue check',
      caption: 'Ruleset 2: required status checks, without the "Merge queue" check.',
    },
    allowEdits: {
      name: 'allow-edits-by-maintainers',
      alt: 'The sidebar of a pull request from a fork on GitHub with Allow edits by maintainers ticked',
      caption: 'A fork\'s pull request: Allow edits by maintainers.',
    },
    accountSettings: {
      name: 'account-merge-queue-settings',
      alt: 'The Merge queue group on a CodeCoverage organization page: Merge queue approval floor, Checks the merge queue ignores and Merge queue bypass role',
      caption: 'CodeCoverage: the organization page\'s Merge queue group.',
    },
  } satisfies Record<string, Screenshot>;

  private readonly query = toSignal(this.route.queryParamMap, { requireSync: true });

  /** The account the guide was opened for, if any: step 1 shows its permissions notice, step 8 links its page. */
  protected readonly account = computed(() => {
    const provider = this.query().get('provider');
    const login = this.query().get('account');
    return provider && login ? { provider, login } : null;
  });

  /** Screenshots whose image failed to load; each renders as a "Screenshot pending" box instead. */
  protected readonly missing = signal<ReadonlySet<MergeQueueSetupScreenshot>>(new Set());

  constructor() {
    afterNextRender(() => {
      const fragment = this.route.snapshot.fragment;
      if (fragment) this.jumpTo(fragment);
    });
  }

  protected jumpTo(id: string): void {
    this.document.getElementById(id)?.scrollIntoView({ block: 'start' });
  }

  /** A screenshot's URL, or none while it is pending. */
  protected urlOf(shot: Screenshot): string | undefined {
    return MERGE_QUEUE_SETUP_SCREENSHOTS[shot.name];
  }

  protected markMissing(name: MergeQueueSetupScreenshot): void {
    this.missing.update(names => new Set(names).add(name));
  }
}
