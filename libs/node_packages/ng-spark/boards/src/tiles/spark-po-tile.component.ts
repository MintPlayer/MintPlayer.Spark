import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { hasShowedOnFlag, PersistentObject, PersistentObjectAttribute, ShowedOn } from '@mintplayer/ng-spark/models';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkService } from '@mintplayer/ng-spark/services';
import { SparkBoardTile, SparkTileContext } from '../spark-board.models';

/**
 * The `persistentObject` kind (D15): one object, read-only, over `SparkService.get`. It lists the
 * attributes the detail page would show, as plain text, and links to that page. It does not reuse
 * `spark-po-detail`, which reads its object from the route.
 */
@Component({
  selector: 'spark-po-tile',
  imports: [RouterLink, ResolveTranslationPipe, TranslateKeyPipe],
  styleUrl: './spark-tiles.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (item(); as po) {
      <dl class="spark-tile-po">
        @for (attribute of attributes(); track attribute.name) {
          <dt>{{ attribute.label ? (attribute.label | resolveTranslation) : attribute.name }}</dt>
          <dd>{{ display(attribute) }}</dd>
        }
      </dl>
      <a class="spark-tile-open" [routerLink]="['/po', entityType(), po.id]">{{ 'boards.spa.open' | t }}</a>
    } @else if (failed()) {
      <span class="spark-tile-muted" role="alert">{{ 'boards.spa.loadFailed' | t }}</span>
    } @else {
      <span class="spark-tile-muted">…</span>
    }
  `,
})
export class SparkPoTileComponent {
  private readonly spark = inject(SparkService);

  readonly tile = input.required<SparkBoardTile>();
  readonly context = input.required<SparkTileContext>();

  protected readonly entityType = computed(() => this.tile().target['entityType'] ?? '');
  protected readonly item = signal<PersistentObject | null>(null);
  protected readonly failed = signal(false);
  protected readonly attributes = computed(() =>
    (this.item()?.attributes ?? []).filter(a => hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject)));

  constructor() {
    effect(onCleanup => {
      const type = this.entityType();
      const id = this.tile().target['id'] ?? '';
      this.context().reloadToken;
      let live = true;
      onCleanup(() => live = false);
      this.failed.set(false);
      this.spark.get(type, id).then(
        po => { if (live) this.item.set(po); },
        () => { if (live) this.failed.set(true); },
      );
    });
  }

  protected display(attribute: PersistentObjectAttribute): string {
    if (attribute.breadcrumb) return attribute.breadcrumb;
    const value = attribute.value;
    if (value === null || value === undefined) return '';
    if (Array.isArray(value)) return value.map(v => attribute.breadcrumbs?.[v] ?? String(v)).join(', ');
    return typeof value === 'object' ? '' : String(value);
  }
}
