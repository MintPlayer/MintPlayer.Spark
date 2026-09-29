import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkDetailContext } from '@mintplayer/ng-spark/panels';
import { SparkPoHistoryComponent } from './spark-po-history.component';

/**
 * The History card on the routed detail page (#460). Registered by {@link provideSparkHistory} as a
 * `SPARK_DETAIL_PANELS` entry; renders nothing for a caller without `History/T`.
 * After a Revert it asks the page to reload, so the attributes above show the reverted values.
 */
@Component({
  selector: 'spark-history-panel',
  imports: [BsCardComponent, BsCardHeaderComponent, TranslateKeyPipe, SparkPoHistoryComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visible()) {
      <bs-card class="d-block spark-history-panel">
        <bs-card-header>{{ 'history.title' | t }}</bs-card-header>
        <div class="p-3">
          <spark-po-history
            [type]="context().type"
            [id]="context().id"
            [entityType]="context().entityType"
            [current]="context().item"
            [permissions]="context().permissions"
            [deleted]="context().deleted"
            (reverted)="context().reload()" />
        </div>
      </bs-card>
    }
  `,
})
export class SparkHistoryPanelComponent {
  context = input.required<SparkDetailContext>();

  protected readonly visible = computed(() => this.context().permissions?.canViewHistory === true);
}
