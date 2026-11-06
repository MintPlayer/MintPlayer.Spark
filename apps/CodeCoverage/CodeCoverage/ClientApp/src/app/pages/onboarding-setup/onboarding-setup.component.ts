import { afterNextRender, ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';

/** A step of the guide. The id is its anchor, kept stable so a message can link `/onboarding/setup#<id>`. */
interface SetupStep {
  id: string;
  title: string;
}

/** One feature the chooser offers, in the order of its form (EOnboardingFeature). */
interface Feature {
  name: string;
  meaning: string;
  pullRequest: string;
  onMerge: string;
  /** The feature's own guide; null when it has none in the app. */
  guide: string | null;
}

/**
 * The getting-started guide (/onboarding/setup): the repository page's **Set up features** chooser, the one pull request it
 * opens on `mintplayer/onboarding`, and what happens when that merges. The decisions behind it are
 * docs/coverage_backport_PRD.md (#571 D2, D3, D9; §8 M10) and the reference is docs/code-coverage/onboarding.md.
 *
 * Built like the other guides (/backports/setup): static documentation, open to anonymous visitors, one card per step
 * with an anchor, and the page scrolls to a `#<step>` fragment itself because the router has no anchor scrolling.
 * The feature table repeats OnboardingPlanner's sections and on-merge settings (CodeCoverage/Onboarding).
 */
@Component({
  selector: 'app-onboarding-setup',
  imports: [RouterLink, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent],
  templateUrl: './onboarding-setup.component.html',
  styleUrl: './onboarding-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class OnboardingSetupComponent {
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  protected readonly steps: SetupStep[] = [
    { id: 'choose', title: 'Choose the features' },
    { id: 'features', title: 'What each feature sets up' },
    { id: 'pull-request', title: 'The onboarding pull request' },
    { id: 'on-merge', title: 'When it merges' },
    { id: 'again', title: 'Running it again' },
    { id: 'by-hand', title: 'What stays for you to do' },
  ];

  protected readonly features: Feature[] = [
    {
      name: 'Code coverage',
      meaning: 'Upload coverage from CI and gate pull requests on it.',
      pullRequest: 'A checklist item with the upload step for your stack: where it goes depends on how your workflows build and test.',
      onMerge: '-',
      guide: '/coverage/setup',
    },
    {
      name: 'Dependency updates',
      meaning: 'One rolling pull request with the dependency updates, instead of Dependabot.',
      pullRequest: 'The dependency-lookup workflow and a dependencies section in coverage.yml; Dependabot\'s file is removed.',
      onMerge: 'Dependency updates are switched on for the repository.',
      guide: '/dependency-updates/setup',
    },
    {
      name: 'Merge queue',
      meaning: 'Merge approved pull requests one after the other, each tested on the latest default branch.',
      pullRequest: 'A mergeQueue section in coverage.yml; with release lines, it queues the supported lines too.',
      onMerge: '-',
      guide: '/merge-queue/setup',
    },
    {
      name: 'Package versioning',
      meaning: 'Version and tag the packages from their changes, on the server.',
      pullRequest: 'The versioning package, the action step in the publish workflow, placeholders for hand-edited versions, and a versioning section.',
      onMerge: 'One seed tag per package at the merge commit, so no package restarts at 0.0.0.',
      guide: null,
    },
    {
      name: 'Release lines',
      meaning: 'Release each platform major from its own release/{ecosystem}-{major}.x branch. Brings package versioning with it.',
      pullRequest: 'versioning.lines in coverage.yml, and the list of line branches that will be renamed.',
      onMerge: 'Old line branches are renamed, and each supported line without the regenerate workflow gets a pull request that adds it.',
      guide: '/backports/setup',
    },
    {
      name: 'Size labels',
      meaning: 'Label every pull request with its size.',
      pullRequest: '-',
      onMerge: 'PR size labels are switched on for the repository.',
      guide: '/pr-size-labels/setup',
    },
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
