import { afterNextRender, ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';

/** A step of the guide. The id is its anchor, kept stable so a message can link `/versioning/setup#<id>`. */
interface SetupStep {
  id: string;
  title: string;
}

/**
 * The package versioning guide (/versioning/setup): how CodeCoverage versions a repository's NuGet and npm packages
 * from git tags, how to onboard, the workflow step, the `versioning:` section of coverage.yml and what to do when a run
 * fails. The design is docs/coverage_package_versioning_PRD.md (#153) and the reference is
 * docs/code-coverage/versioning.md; release lines and backports have their own guide (/backports/setup).
 *
 * Built like the other guides (/pr-size-labels/setup): static documentation, open to anonymous visitors, one card per
 * step with an anchor, and the page scrolls to a `#<step>` fragment itself because the router has no anchor scrolling
 * (app.config.ts). The workflow snippet, the keys and the troubleshooting rows repeat versioning.md; keep them in step.
 */
@Component({
  selector: 'app-versioning-setup',
  imports: [RouterLink, SparkIconComponent, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent],
  templateUrl: './versioning-setup.component.html',
  styleUrl: './versioning-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class VersioningSetupComponent {
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  protected readonly steps: SetupStep[] = [
    { id: 'how', title: 'How versions are decided' },
    { id: 'onboard', title: 'Onboard the repository' },
    { id: 'workflow', title: 'The workflow step' },
    { id: 'configure', title: 'Configure it in coverage.yml' },
    { id: 'releases', title: 'GitHub Releases and the commit graph' },
    { id: 'troubleshoot', title: 'Troubleshooting' },
  ];

  /** The publish workflow's versioning steps (versioning.md, "The workflow step"). */
  protected readonly workflow = [
    'permissions:',
    '  contents: write   # push the version tags',
    '  id-token: write   # proves to CodeCoverage which repository and workflow is calling',
    '',
    'steps:',
    '  - uses: actions/checkout@v7',
    '    with:',
    '      fetch-depth: 0',
    '  - run: dotnet build -c Release',
    '  - run: dotnet test -c Release --no-build',
    '  - id: versioning',
    '    uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1',
    '  - run: dotnet pack -c Release -o nupkgs',
    '  - run: dotnet nuget push "nupkgs/*.nupkg" --skip-duplicate ...',
  ].join('\n');

  /** The keys of the `versioning:` section (versioning.md, "coverage.yml reference"). */
  protected readonly keys = [
    { key: 'mode', meaning: 'server (the default): CodeCoverage computes the versions and pushes the tags. local: the runner does, with the workflow\'s token.' },
    { key: 'prerelease', meaning: 'upstream (the default) mirrors the label of the platform or a dependency. preview, rc, … pin a label; stable releases without one.' },
    { key: 'groups', meaning: 'Packages that always release together, at the group\'s highest version.' },
    { key: 'packages.<id>.anchor', meaning: 'free keeps the major of the last tag; or the id of the package whose major it follows.' },
    { key: 'ignore', meaning: 'Changed files that release nothing. This list replaces the default.' },
    { key: 'dispatch', meaning: 'A workflow CodeCoverage starts after a merge that changed a package.' },
    { key: 'dependencyRanges', meaning: 'minimum (>= x, the default) or major-bounded ([x, next major)) for in-repository dependencies.' },
    { key: 'lines', meaning: 'Release only from release lines, one per ecosystem and platform major. See the backports guide.' },
  ];

  /** Symptom and fix (versioning.md, "Troubleshooting"). */
  protected readonly problems = [
    { symptom: 'MPV001: No tag is reachable from HEAD', fix: 'The package has no tag yet, or the checkout is shallow: give it fetch-depth: 0.' },
    { symptom: 'A package restarted at X.0.0', fix: 'It had no seed tag. Run Set up features again; it tags the versions the history declares.' },
    { symptom: 'MPV006: on line N.x, the package has no tag of major N', fix: 'Run Set up features again: it tags every version the history declares.' },
    { symptom: 'The run fails: "a major never goes down"', fix: 'The computed major is below the last tag\'s. Anchor the package free, or fix the target framework.' },
    { symptom: 'phase: release fails with stale-plan', fix: 'A tag changed between the plan and the release. Use "Re-run all jobs", which plans again.' },
    { symptom: 'The run answers 503 seeds', fix: 'The onboarding\'s seed tags are not pushed yet; the run waits rather than release untagged packages.' },
    { symptom: 'Nothing is released after a merge', fix: 'No input of any package changed: only ignored files, or files outside the project directories.' },
    { symptom: 'The run failed after the Version step', fix: 'Re-run it. The re-run finds its own tags, releases the same versions and pushes what is missing.' },
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
