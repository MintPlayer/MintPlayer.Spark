import { afterNextRender, ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { coverageSetupGuides } from './coverage-setup.snippets';

/** A step of the guide. The id is its anchor, kept stable so a message can link `/coverage/setup#<id>`. */
interface SetupStep {
  id: string;
  title: string;
}

/**
 * Setting up coverage uploads (/coverage/setup): the CI workflow that uploads a repository's reports, per ecosystem. It
 * used to be the "Set up coverage uploads" panel on every repository page; it moved here (PR size labels PRD D10), and
 * the repository page links it while the repository has no coverage yet.
 *
 * Built like the other guides (/merge-queue/setup): static documentation, open to anonymous visitors, one card per step
 * with an anchor, and the page scrolls to a `#<step>` fragment itself because the router has no anchor scrolling
 * (app.config.ts). The workflows name this deployment's own address, so they are copied as they are.
 */
@Component({
  selector: 'app-coverage-setup',
  imports: [
    RouterLink, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsCodeSnippetComponent,
    BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective,
  ],
  templateUrl: './coverage-setup.component.html',
  styleUrl: './coverage-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class CoverageSetupComponent {
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  protected readonly primaryColor = Color.primary;

  protected readonly steps: SetupStep[] = [
    { id: 'install', title: 'Install the GitHub App' },
    { id: 'workflow', title: 'Add the upload to your CI' },
    { id: 'private', title: 'Private repositories: an upload token' },
    { id: 'results', title: 'Where the results show' },
  ];

  /**
   * One forge configured: its per-language tabs render directly, with the intro above them, rather than inside a
   * forge tab strip with one item in it.
   */
  protected readonly guides = coverageSetupGuides(this.document.location.origin);
  protected readonly singleGuide = this.guides.length === 1;

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
