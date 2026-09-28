import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import { SparkQueryListContext } from '@mintplayer/ng-spark/panels';

/**
 * The query page's "Deleted" toggle (#460): switches the list into the recycle bin
 * (`deleted: only`) and back. Rendered only for holders of `ViewDeleted/T` — for anyone else the
 * server would ignore the widening anyway, so offering it would show the same rows under a
 * misleading label.
 *
 * Registered by {@link provideSparkSoftDelete} as a `SPARK_QUERY_LIST_ACTIONS` entry; usable on its
 * own in a custom page with any {@link SparkQueryListContext}.
 */
@Component({
  selector: 'spark-deleted-toggle',
  imports: [TranslateKeyPipe, SparkIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visible()) {
      <button type="button"
              class="btn spark-deleted-toggle"
              [class.btn-secondary]="active()"
              [class.btn-outline-secondary]="!active()"
              [attr.aria-pressed]="active()"
              (click)="toggle()">
        <spark-icon name="trash" /> {{ 'softDelete.showDeleted' | t }}
      </button>
    }
  `,
})
export class SparkDeletedToggleComponent {
  context = input.required<SparkQueryListContext>();

  protected readonly visible = computed(() =>
    !!this.context().entityType && this.context().permissions?.canViewDeleted === true);

  protected readonly active = computed(() => this.context().deleted === 'only');

  toggle(): void {
    this.context().setDeleted(this.active() ? 'exclude' : 'only');
  }
}
