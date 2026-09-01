import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterModule } from '@angular/router';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { valueFor } from '@mintplayer/ng-spark/models';

/**
 * Spark attribute renderer "short-sha": a commit sha as its 7-char short form
 * (master-parity "Latest commit" cell). With the item row context (Spark#245)
 * it links to the vanity commit page, derived from the row's FullName
 * ("owner/name"); without it, plain text.
 */
@Component({
  selector: 'app-short-sha-renderer',
  imports: [RouterModule],
  template: `
    @if (shortSha(); as sha) {
      @if (commitRoute(); as route) {
        <a [routerLink]="route" class="font-monospace small" [title]="tooltip() ?? ''">{{ sha }}</a>
      } @else {
        <span class="font-monospace small" [title]="tooltip() ?? ''">{{ sha }}</span>
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShortShaRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();
  item = input<any>();

  readonly shortSha = computed(() => {
    const value = this.value();
    return typeof value === 'string' && value.length > 0 ? value.substring(0, 7) : null;
  });

  /** rendererOptions.titleAttribute names a sibling attribute whose value becomes the tooltip (e.g. the commit message). */
  readonly tooltip = computed(() => {
    const titleAttribute = this.options()?.['titleAttribute'];
    if (typeof titleAttribute !== 'string') return null;
    const title = valueFor(this.item(), titleAttribute)?.value;
    return typeof title === 'string' && title ? title : null;
  });

  readonly commitRoute = computed(() => {
    const sha = this.value();
    const fullName = valueFor(this.item(), 'FullName')?.value;
    if (typeof sha !== 'string' || typeof fullName !== 'string') return null;
    const [owner, name] = fullName.split('/');
    return owner && name ? ['/r', owner, name, 'c', sha] : null;
  });
}
