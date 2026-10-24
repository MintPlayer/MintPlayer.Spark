import { afterNextRender, ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';

/** A step of the guide. The id is its anchor, kept stable so a message can link `/pr-size-labels/setup#<id>`. */
interface SetupStep {
  id: string;
  title: string;
}

/**
 * The PR size labeller's guide (/pr-size-labels/setup): what it does, switching it on, the scale, the ignore rules, the
 * "PR size" check run and switching it off. The decisions behind it are docs/coverage_pr_size_labels_PRD.md D1–D8 (the
 * contents are D10's list), and the reference is docs/code-coverage/pr-size-labels.md.
 *
 * Built like the other guides (/merge-queue/setup): static documentation, open to anonymous visitors, one card per step
 * with an anchor, and the page scrolls to a `#<step>` fragment itself because the router has no anchor scrolling
 * (app.config.ts). The tables repeat the defaults of PrSizeDefaults (CodeCoverage.Library/Entities/PrSizeLabel.cs).
 */
@Component({
  selector: 'app-pr-size-labels-setup',
  imports: [RouterLink, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent],
  templateUrl: './pr-size-labels-setup.component.html',
  styleUrl: './pr-size-labels-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class PrSizeLabelsSetupComponent {
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  protected readonly primaryColor = Color.primary;
  protected readonly infoColor = Color.info;

  protected readonly steps: SetupStep[] = [
    { id: 'enable', title: 'Turn it on for the account or a repository' },
    { id: 'scale', title: 'The size scale' },
    { id: 'ignore', title: 'Which files count' },
    { id: 'check-run', title: 'The "PR size" check' },
    { id: 'disable', title: 'Turning it off' },
    { id: 'adopt', title: 'Coming from a size-label GitHub Action' },
    { id: 'forges', title: 'Forges' },
  ];

  /** The scale a new account starts with (PRD D2), the one pascalgn/size-label-action uses. */
  protected readonly scale = [
    { name: 'size/XS', minLines: 0, color: '#009900' },
    { name: 'size/S', minLines: 10, color: '#77bb00' },
    { name: 'size/M', minLines: 30, color: '#eebb00' },
    { name: 'size/L', minLines: 100, color: '#ee9900' },
    { name: 'size/XL', minLines: 500, color: '#ee5500' },
    { name: 'size/XXL', minLines: 1000, color: '#ee0000' },
  ];

  /** The ignore rules a new account starts with (PRD D5), in their order: the last matching rule wins. */
  protected readonly ignoreRules = [
    { glob: '**/*.json', mode: 'Ignore additions' },
    { glob: '**/*Tests/**', mode: 'Ignore additions' },
    { glob: '**/package-lock.json', mode: 'Ignore' },
    { glob: '**/packages.lock.json', mode: 'Ignore' },
    { glob: '**/Directory.Packages.props', mode: 'Ignore' },
    { glob: '**/*.md', mode: 'Ignore' },
  ];

  /** What a repository's own setting can say (EPrSizeLabelPolicy). */
  protected readonly policies = [
    { value: 'Use account setting', meaning: 'Labels while the account\'s "PR size labels" is on. New repositories start here.' },
    { value: 'Label pull requests', meaning: 'Labels, whatever the account says.' },
    { value: 'Never label', meaning: 'Never labels, whatever the account says.' },
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
