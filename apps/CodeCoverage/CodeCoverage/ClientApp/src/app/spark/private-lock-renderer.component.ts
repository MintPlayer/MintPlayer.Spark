import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { isPersistentObject } from '@mintplayer/ng-spark/models';

/**
 * Spark attribute renderer "private-lock", on `Repository.IsPrivate`: a lock glyph for a private
 * repository, and nothing for a public one, so the grid gets a narrow 🔒 column.
 *
 * It replaced the inline "private" badge `repo-name` drew from a sibling `IsPrivate` value. That
 * needed `IsPrivate` shipped in every row but not drawn as a column, which `showedOn` cannot say
 * since `isVisible` was deleted (#264, G-Q4: a value reaches a grid row only as a column). Making the
 * flag its own column is the honest form of the same thing.
 *
 * On the detail page the row also says it in words, because a lone glyph next to a "Private" label
 * reads as decoration, and an empty value for a public repository reads as missing data.
 */
@Component({
  selector: 'app-private-lock-renderer',
  template: `
    @if (isPrivate()) {
      <i class="bi bi-lock-fill" title="private" aria-label="private"></i>
      @if (detail()) { <span class="ms-1">private</span> }
    } @else if (detail()) {
      <span>public</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class PrivateLockRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  options = input<Record<string, any> | undefined>();
  item = input<any>();

  readonly isPrivate = computed(() => this.value() === true);

  /** The detail page hands the persistent object itself; a grid hands a query row. */
  readonly detail = computed(() => isPersistentObject(this.item()));
}
