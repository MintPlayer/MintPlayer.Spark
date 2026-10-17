import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { SparkAttributeColumnRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark column renderer "pull-request-link", on `DependencyOnboardingRow.OnboardingPullRequest` (dependency-updates PRD
 * D-O14): the onboarding pull request as `#n`, linking to it on the forge.
 *
 * The server sends the pull request's URL, or `#n` when the forge gives none; a plain `url` column would print the whole
 * URL. The number is read from the URL's last path segment, so the forge's URL scheme stays the server's business.
 */
@Component({
  selector: 'app-pull-request-link-renderer',
  template: `
    @if (href(); as url) {
      <a [href]="url" target="_blank" rel="noopener noreferrer">{{ label() }}</a>
    } @else if (label(); as text) {
      {{ text }}
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PullRequestLinkRendererComponent implements SparkAttributeColumnRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();
  item = input<any>();

  /** Only an http(s) URL becomes a link: these values are data. */
  readonly href = computed(() => {
    const value = this.value();
    return typeof value === 'string' && /^https?:\/\//i.test(value) ? value : null;
  });

  readonly label = computed(() => {
    const value = this.value();
    if (typeof value !== 'string' || value.length === 0) return null;
    if (!this.href()) return value;
    const number = /\/(\d+)\/?$/.exec(value)?.[1];
    return number ? `#${number}` : value;
  });
}
