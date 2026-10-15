import { NgTemplateOutlet } from '@angular/common';
import { afterNextRender, ChangeDetectionStrategy, Component, computed, DOCUMENT, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { AccountPermissionsNoticeComponent } from '../../components/account-permissions-notice/account-permissions-notice.component';
import { MERGE_QUEUE_SETUP_SCREENSHOTS, type MergeQueueSetupAttachment, type MergeQueueSetupScreenshot } from './merge-queue-setup.screenshots';
import { COVERAGE_YML, CREATE_LABEL, CREATE_RULESETS, PUSH_TRIGGER, REGENERATE_WORKFLOW, RULESET_1, RULESET_2 } from './merge-queue-setup.snippets';

/** A step of the guide. The id is its anchor, kept stable so a message can link `/merge-queue/setup#<id>`. */
interface SetupStep {
  id: string;
  title: string;
}

/**
 * A screenshot slot. Its image, a GitHub attachment with its size in pixels, is in MERGE_QUEUE_SETUP_SCREENSHOTS, whose
 * file also says what each screenshot shows and how it was captured.
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
 * Screenshots are GitHub attachments (O23) and use native `loading="lazy"` with explicit `width`/`height` (O13), each
 * the image's own size. One without a URL yet is a "Screenshot pending" box, and nothing is requested for it; one that
 * fails to load becomes the same box at the image's size, never a broken image.
 */
@Component({
  selector: 'app-merge-queue-setup',
  imports: [
    NgTemplateOutlet, RouterLink, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent,
    BsCodeSnippetComponent, BsTableComponent, AccountPermissionsNoticeComponent,
  ],
  templateUrl: './merge-queue-setup.component.html',
  styleUrl: './merge-queue-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class MergeQueueSetupComponent {
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  protected readonly primaryColor = Color.primary;
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
    { name: 'Checks', access: 'Read and write', isNew: false, use: 'Publish the "Merge queue" check run and its Add to merge queue button; read every other check.' },
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
      alt: 'GitHub asking an organization owner to accept updated permissions for the CodeCoverage app: the requested repository permission, marked New request, and the Accept new permissions button',
      caption: 'GitHub: the installation\'s review page. It lists whatever the App is requesting at the time; the request shown is an example.',
    },
    permissionsBanner: {
      name: 'app-pending-permissions-banner',
      alt: 'The notice on a CodeCoverage organization page: GitHub is waiting for you to accept new permissions, the pending permission, and the links Review and accept on GitHub and Merge queue setup guide',
      caption: 'CodeCoverage: what the organization page shows while a permission request is pending.',
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
      alt: 'Ruleset 1\'s bypass list on GitHub, the Repository admin role allowed for pull requests only and the CodeCoverage App exempt, above its target, the default branch',
      caption: 'Ruleset 1: the bypass list and the target branches.',
    },
    ruleset1Rules: {
      name: 'ruleset-1-rules',
      alt: 'Ruleset 1\'s branch rules on GitHub: Restrict updates ticked, and every other rule unticked, Require a pull request before merging and Require status checks to pass among them',
      caption: 'Ruleset 1: Restrict updates is its only branch rule.',
    },
    ruleset2PullRequest: {
      name: 'ruleset-2-pull-request',
      alt: 'Ruleset 2\'s Require a pull request before merging rule on GitHub, with Dismiss stale pull request approvals and Require approval of the most recent reviewable push unticked',
      caption: 'Ruleset 2: required reviews, both approval settings off.',
    },
    ruleset2StatusChecks: {
      name: 'ruleset-2-status-checks',
      alt: 'Ruleset 2\'s Require status checks to pass rule on GitHub, with Require branches to be up to date before merging ticked and the repository\'s CI check required, not the Merge queue check',
      caption: 'Ruleset 2: required status checks, without the "Merge queue" check.',
    },
    allowEdits: {
      name: 'allow-edits-by-maintainers',
      alt: 'The form that opens a pull request from a fork on GitHub, with Allow edits by maintainers ticked beside the Create pull request button',
      caption: 'Opening a pull request from a fork: Allow edits by maintainers.',
    },
    accountSettings: {
      name: 'account-merge-queue-settings',
      alt: 'The Merge queue group on a CodeCoverage organization page at its defaults: Merge queue approval floor on Approval for external authors, no Checks the merge queue ignores, Merge queue bypass role on Nobody',
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

  /** A screenshot's image, its URL and size, or none while it is pending. */
  protected imageOf(shot: Screenshot): MergeQueueSetupAttachment | undefined {
    return MERGE_QUEUE_SETUP_SCREENSHOTS[shot.name];
  }

  protected markMissing(name: MergeQueueSetupScreenshot): void {
    this.missing.update(names => new Set(names).add(name));
  }
}
