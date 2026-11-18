import { afterNextRender, ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';

/** A step of the guide. The id is its anchor, kept stable so a message can link `/project-boards/setup#<id>`. */
interface SetupStep {
  id: string;
  title: string;
}

/** A group of rule events, as the rule's Event dropdown lists them (WebhookEventType.Items). */
interface EventGroup {
  name: string;
  events: string[];
  moves: string;
}

/**
 * The project board automation guide (/project-boards/setup, #625): GitHub Projects boards whose cards move between
 * the Status columns when an issue or pull request changes. The design is docs/coverage_project_automation_PRD.md and
 * the decisions are docs/decisions_messaging_and_project_automation.md; the reference is
 * docs/code-coverage/project-boards.md.
 *
 * Built like the other guides (/pr-size-labels/setup): static documentation, open to anonymous visitors, one card per
 * step with an anchor, and the page scrolls to a `#<step>` fragment itself because the router has no anchor scrolling
 * (app.config.ts). The event table repeats WebhookEventType.Items (CodeCoverage.Library/LookupReferences), and the
 * rule options repeat EventColumnMapping's defaults (CodeCoverage.Library/Entities/EventColumnMapping.cs).
 */
@Component({
  selector: 'app-project-boards-setup',
  imports: [RouterLink, SparkIconComponent, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent],
  templateUrl: './project-boards-setup.component.html',
  styleUrl: './project-boards-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class ProjectBoardsSetupComponent {
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  protected readonly steps: SetupStep[] = [
    { id: 'install', title: 'Give the GitHub App access to your boards' },
    { id: 'board', title: 'Prepare the board' },
    { id: 'find', title: 'Find the board in CodeCoverage' },
    { id: 'enable', title: 'Switch automation on and read the columns' },
    { id: 'rules', title: 'Add rules' },
    { id: 'events', title: 'Which card each event moves' },
    { id: 'columns', title: 'When you change the columns' },
    { id: 'troubleshoot', title: 'When a card does not move' },
  ];

  /** The events a rule can react to, in the Event dropdown's order (WebhookEventType.Items). */
  protected readonly eventGroups: EventGroup[] = [
    {
      name: 'Issues',
      events: ['Issue opened', 'Issue closed', 'Issue reopened', 'Issue labeled', 'Issue unlabeled', 'Issue assigned', 'Issue unassigned'],
      moves: 'The issue\'s card.',
    },
    {
      name: 'Pull requests',
      events: ['Pull request opened', 'Pull request closed without merging', 'Pull request merged', 'PR ready for review', 'PR converted to draft', 'PR review requested'],
      moves: 'The pull request\'s card, and the cards of the issues it closes.',
    },
    {
      name: 'Reviews',
      events: ['PR review: approved', 'PR review: changes requested', 'PR review dismissed'],
      moves: 'The pull request\'s card, and the cards of the issues it closes.',
    },
    {
      name: 'Checks',
      events: ['Check run completed'],
      moves: 'The card of each pull request the check ran for, and the issues they close.',
    },
    {
      name: 'Comments',
      events: ['Issue comment created'],
      moves: 'The card of the issue or pull request that was commented on.',
    },
  ];

  /** A rule's fields (EventColumnMapping); the three switches start on. */
  protected readonly ruleFields = [
    { field: 'Event', meaning: 'The GitHub event the rule reacts to. A board has at most one rule per event.' },
    { field: 'Target Column', meaning: 'The column the card moves to, chosen from the board\'s columns.' },
    { field: 'Enabled', meaning: 'Turns this one rule off without deleting it. On for a new rule.' },
    { field: 'Move Linked Issues', meaning: 'For pull request, review and check events: also move the issues the pull request closes. On for a new rule.' },
    { field: 'Add To Board', meaning: 'Adds the issue or pull request to the board first when it is not on it yet. On for a new rule.' },
  ];

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
