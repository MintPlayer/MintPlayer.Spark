import { afterNextRender, ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';

/** A step of the guide. The id is its anchor, kept stable so a message can link `/commit-graph/setup#<id>`. */
interface SetupStep {
  id: string;
  title: string;
}

/**
 * The commit graph guide (/commit-graph/setup): the graph on the repository page, what its nodes and tools mean, how it
 * stays current and why it can be unavailable. Nothing to switch on: it is a reading guide. The design is
 * docs/coverage_commit_graph_PRD.md (#504) and the reference is docs/code-coverage/commit-graph.md.
 *
 * Built like the other guides (/pr-size-labels/setup): static documentation, open to anonymous visitors, one card per
 * step with an anchor, and the page scrolls to a `#<step>` fragment itself because the router has no anchor scrolling
 * (app.config.ts). The limits table repeats the defaults in commit-graph.md §4, and the reasons repeat its list of
 * `unavailable` reasons.
 */
@Component({
  selector: 'app-commit-graph-setup',
  imports: [RouterLink, SparkIconComponent, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent],
  templateUrl: './commit-graph-setup.component.html',
  styleUrl: './commit-graph-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class CommitGraphSetupComponent {
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  protected readonly steps: SetupStep[] = [
    { id: 'where', title: 'Where to find it' },
    { id: 'reading', title: 'Reading the graph' },
    { id: 'tools', title: 'Finding a commit' },
    { id: 'releases', title: 'Releases on the graph' },
    { id: 'current', title: 'How it stays current' },
    { id: 'unavailable', title: 'When the graph is unavailable' },
    { id: 'privacy', title: 'Who sees it' },
  ];

  /** How a node is drawn (commit-graph.md §1). */
  protected readonly nodes = [
    { node: 'Filled', meaning: 'The commit has a coverage report; the fill follows its coverage, on a scale from low to high.' },
    { node: 'Ring', meaning: 'No coverage report was uploaded for this commit.' },
    { node: 'Hollow grey', meaning: 'A report is still being processed, or only part of the expected reports arrived.' },
  ];

  /** The repository-size limits a mirror is held to (commit-graph.md §4, the defaults). */
  protected readonly limits = [
    { kind: 'Repository with the GitHub App installed', size: '2 GB', refs: '50,000', time: '10 minutes' },
    { kind: 'Public repository without the App', size: '500 MB', refs: '10,000', time: '3 minutes' },
  ];

  /** Why a graph can be unavailable (the `unavailable` reasons). */
  protected readonly reasons = [
    { reason: 'too-many-refs', meaning: 'The repository has more branches and tags than the limit.' },
    { reason: 'too-large', meaning: 'Its history is larger than the limit.' },
    { reason: 'timeout', meaning: 'Copying its history took longer than the limit.' },
    { reason: 'disk-floor', meaning: 'The server is short of disk space; this is on our side.' },
    { reason: 'auth', meaning: 'GitHub refused the App access to the repository.' },
    { reason: 'not-found', meaning: 'GitHub no longer has the repository.' },
    { reason: 'error', meaning: 'Something else went wrong while copying it.' },
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
