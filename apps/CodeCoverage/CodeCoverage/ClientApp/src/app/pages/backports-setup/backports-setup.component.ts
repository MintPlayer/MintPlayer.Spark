import { afterNextRender, ChangeDetectionStrategy, Component, DOCUMENT, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsCardBodyComponent, BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';

/** A step of the guide. The id is its anchor, kept stable so a message can link `/backports/setup#<id>`. */
interface SetupStep {
  id: string;
  title: string;
}

/**
 * The backports guide (/backports/setup): release lines, asking for a backport, what happens on each line, the comment
 * that comes back, backporting by hand, and the `backport:` section of coverage.yml. The decisions behind it are
 * docs/coverage_backport_PRD.md (#568, #570), and the reference is docs/code-coverage/backports.md.
 *
 * Built like the other guides (/pr-size-labels/setup): static documentation, open to anonymous visitors, one card per
 * step with an anchor, and the page scrolls to a `#<step>` fragment itself because the router has no anchor scrolling
 * (app.config.ts). The option table repeats BackportYml's defaults (CodeCoverage/PackageVersioning).
 */
@Component({
  selector: 'app-backports-setup',
  imports: [RouterLink, BsAlertComponent, BsBadgeComponent, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent],
  templateUrl: './backports-setup.component.html',
  styleUrl: './backports-setup.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export default class BackportsSetupComponent {
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  protected readonly steps: SetupStep[] = [
    { id: 'release-lines', title: 'Release lines' },
    { id: 'ask', title: 'Asking for a backport' },
    { id: 'each-line', title: 'What happens on each line' },
    { id: 'comment', title: 'The comment you get back' },
    { id: 'by-hand', title: 'Conflicts and backports by hand' },
    { id: 'configure', title: 'The backport section of coverage.yml' },
    { id: 'protect', title: 'Protect your release branches' },
  ];

  /** The ways to ask (D5, D7): each needs write access to the repository. */
  protected readonly requests = [
    { how: 'backport: 8.x', meaning: 'A label on the pull request, before or after it merges. Labels already there when it merges are backported on the merger\'s behalf.' },
    { how: '/backport 8.x', meaning: 'A comment on the merged pull request, on its first line. Up to ten lines may follow, separated by spaces or commas.' },
    { how: '/backport 8.x --draft', meaning: 'The same, but a line that conflicts is pushed anyway, as a draft pull request with the conflict markers committed.' },
  ];

  /** The `backport:` options and their defaults (BackportYml). */
  protected readonly options = [
    { key: 'onConflict', values: 'comment | draft', defaultValue: 'comment', meaning: 'What a conflicting line gets: only the comment, or a draft pull request with the conflict markers committed.' },
    { key: 'enqueue', values: 'true | false', defaultValue: 'true', meaning: 'Hand each backport pull request to the merge queue, when the queue serves its line.' },
    { key: 'defaultLabels', values: 'all | fixes | off', defaultValue: 'all', meaning: 'Which backport labels a new pull request into the default branch gets.' },
    { key: 'fixLabels', values: 'labels', defaultValue: '(none)', meaning: 'Labels that mark a fix under fixes, beside a fix: title.' },
    { key: 'lines', values: 'line: keep | retire', defaultValue: '(none)', meaning: 'Keep a line supported past its platform\'s end of life, or retire one.' },
    { key: 'markers.disable', values: 'kinds', defaultValue: '(none)', meaning: 'Leave a built-in kind of platform value to the plain merge.' },
    { key: 'markers.packages', values: 'names, * as a wildcard', defaultValue: '(none)', meaning: 'Your own packages whose major follows the platform, NuGet or npm.' },
  ];

  /** The example section. It sits in a string because a template would read its `@` as a block. */
  protected readonly example = [
    'backport:',
    '  onConflict: draft          # comment (default) | draft',
    '  enqueue: false             # default true',
    '  defaultLabels: fixes       # all (default) | fixes | off',
    '  fixLabels: [bug]',
    '  lines:',
    '    dotnet-8.x: keep         # supported past its platform\'s end of life',
    '    angular-20.x: retire     # no longer supported',
    '  markers:',
    '    disable: [setup-action]  # tfm | framework-packages | global.json | setup-action | npm-frameworks',
    '    packages: ["@mintplayer/ng-bootstrap", "MyCompany.*"]',
  ].join('\n');

  /** The commands for a manual backport, as the conflict comment gives them. */
  protected readonly byHand = [
    'git fetch origin',
    'git switch -c backport/123-8.x origin/release/8.x',
    'git cherry-pick -x <merge commit>',
    'git push -u origin backport/123-8.x',
  ].join('\n');

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
