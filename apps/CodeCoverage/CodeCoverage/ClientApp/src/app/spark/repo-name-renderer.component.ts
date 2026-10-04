import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark attribute renderer "repo-name": the repository name.
 *
 * It used to append master's inline "private" badge from a sibling `IsPrivate` value, which only
 * worked while `IsPrivate` was shipped in every row without being a column. That trick went with
 * `isVisible` (#264, G-Q4): `IsPrivate` is now its own narrow 🔒 column, drawn by the
 * `private-lock` renderer, and this renderer draws just the name.
 */
@Component({
  selector: 'app-repo-name-renderer',
  template: `{{ value() }}`,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoNameRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();
  item = input<any>();
}
