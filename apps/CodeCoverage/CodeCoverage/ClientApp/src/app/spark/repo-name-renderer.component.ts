import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { valueFor } from '@mintplayer/ng-spark/models';

/**
 * Spark attribute renderer "repo-name": the repository name with master's
 * inline "private" badge — a cross-field cell (Name + IsPrivate) enabled by
 * the item row context (Spark#245).
 *
 * ⚠️ The badge depends on `Repository.IsPrivate` being marked
 * `"showedOn": "Query, PersistentObject"` with `"isVisible": false` in
 * `App_Data/Model/Repository.json` — shipped to the row, never drawn as a column.
 * Since preview.67 a grid row carries only the query surface, so narrowing that
 * attribute back to `PersistentObject` removes the badge from every grid with
 * nothing wrong here and nothing wrong in the model: `valueFor` simply returns
 * undefined and a private repository renders as if it were public. The note lives
 * here because `--spark-synchronize-model` strips unknown keys from generated
 * model files, so it cannot live beside the attribute itself.
 */
@Component({
  selector: 'app-repo-name-renderer',
  imports: [BsBadgeComponent],
  template: `
    {{ value() }}
    @if (isPrivate()) {
      <bs-badge class="text-bg-secondary ms-2">private</bs-badge>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoNameRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();
  item = input<any>();

  readonly isPrivate = computed(() => valueFor(this.item(), 'IsPrivate')?.value === true);
}
