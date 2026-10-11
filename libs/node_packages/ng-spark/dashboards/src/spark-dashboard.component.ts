import { ChangeDetectionStrategy, Component, computed, inject, input, LOCALE_ID, Type } from '@angular/core';
import { NgComponentOutlet } from '@angular/common';
import { BsCardBodyComponent, BsCardComponent } from '@mintplayer/ng-bootstrap/card';
import { EntityAttributeDefinition, hasShowedOnFlag, PersistentObject, ShowedOn, TranslatedString } from '@mintplayer/ng-spark/models';
import type { SparkDetailContext } from '@mintplayer/ng-spark/panels';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { rendererValue, SPARK_ATTRIBUTE_RENDERERS, withDeclaredInputs } from '@mintplayer/ng-spark/renderers';
import { formatSparkDashboardValue } from './dashboard-format';

/** The `metadata` key a dashboard carries (the dashboards package's `SparkDashboardActions.MetadataKey`). */
export const SPARK_DASHBOARD_METADATA_KEY = 'dashboard';

/** Whether `item` is a dashboard: the server marks one with `metadata.dashboard`. */
export function isSparkDashboard(item: PersistentObject | null | undefined): boolean {
  const marker = item?.metadata?.[SPARK_DASHBOARD_METADATA_KEY];
  return marker !== null && typeof marker === 'object';
}

interface DashboardTile {
  attribute: EntityAttributeDefinition;
  /** Columns spanned: 1, 2, or the whole row. */
  span: 1 | 2 | 'row';
  component: Type<any> | null;
  inputs: Record<string, any>;
  /** The value as text, for an attribute without a detail renderer. */
  text: string;
}

interface DashboardSection {
  id: string;
  name: string | null;
  label?: TranslatedString;
  tiles: DashboardTile[];
}

/**
 * The dashboard tile layout (D12): replaces a dashboard's attribute cards on the routed detail page
 * (`SPARK_DETAIL_LAYOUTS`, registered by `provideSparkDashboards()`). Each group of the model is a section, each
 * attribute a tile: its label above its detail renderer (`spark-stat`, `spark-meter`, `spark-trend`,
 * `spark-dashboard-table`, …) or its plain value. `columnSpan` 2 takes two columns, 3 and more (and a table) the
 * whole row. An attribute the caller may not read is not on the object and has no tile. The page follows the object
 * over `/spark/live` like any live type, so the tiles change in place.
 */
@Component({
  selector: 'spark-dashboard',
  imports: [NgComponentOutlet, BsCardComponent, BsCardBodyComponent, ResolveTranslationPipe, TranslateKeyPipe],
  template: `
    <div class="spark-dashboard mx-3 my-2">
      @for (section of sections(); track section.id) {
        <section class="mb-3" [attr.data-section]="section.name">
          @if (section.name) {
            <h3 class="h6 text-body-secondary mb-2">{{ section.label | resolveTranslation:section.name }}</h3>
          }
          <div class="spark-dashboard-tiles">
            @for (tile of section.tiles; track tile.attribute.id) {
              <bs-card class="d-block h-100" [class.spark-dashboard-span-2]="tile.span === 2"
                       [class.spark-dashboard-span-row]="tile.span === 'row'" [attr.data-tile]="tile.attribute.name">
                <bs-card-body>
                  <div class="small text-body-secondary mb-1">{{ tile.attribute.label | resolveTranslation:tile.attribute.name }}</div>
                  @if (tile.component) {
                    <ng-container *ngComponentOutlet="tile.component; inputs: tile.inputs" />
                  } @else {
                    <div class="fs-4 fw-semibold text-break">{{ tile.text }}</div>
                  }
                </bs-card-body>
              </bs-card>
            }
          </div>
        </section>
      }
      @if (generatedAt(); as at) {
        <p class="small text-body-secondary mb-0" data-generated>{{ 'dashboards.updated' | t }} {{ at }}</p>
      }
    </div>
  `,
  styles: [`
    .spark-dashboard-tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(12rem, 100%), 1fr)); gap: .75rem; }
    .spark-dashboard-tiles > * { min-width: 0; }
    .spark-dashboard-span-row { grid-column: 1 / -1; }
    /* Two columns only where two fit: on a phone the grid has one, and span 2 would widen the page. */
    @media (min-width: 576px) { .spark-dashboard-span-2 { grid-column: span 2; } }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkDashboardComponent {
  private readonly locale = inject(LOCALE_ID);
  private readonly renderers = inject(SPARK_ATTRIBUTE_RENDERERS);

  readonly context = input.required<SparkDetailContext>();

  protected readonly sections = computed<DashboardSection[]>(() => {
    const { item, entityType } = this.context();
    const loaded = new Map(item.attributes.map(a => [a.name, a] as const));
    const formData: Record<string, any> = {};
    for (const a of item.attributes) formData[a.name] = rendererValue(a);

    const tiles = (entityType.attributes ?? [])
      .filter(a => loaded.has(a.name) && hasShowedOnFlag(loaded.get(a.name)?.showedOn ?? a.showedOn, ShowedOn.PersistentObject))
      .sort((a, b) => a.order - b.order)
      .map(attribute => this.tile(attribute, item, formData));

    const groups = [...(entityType.groups ?? [])].sort((a, b) => a.order - b.order);
    const groupIds = new Set(groups.map(g => g.id));
    const sections: DashboardSection[] = [];
    const ungrouped = tiles.filter(t => !t.attribute.group || !groupIds.has(t.attribute.group));
    if (ungrouped.length > 0) sections.push({ id: '__ungrouped__', name: null, tiles: ungrouped });
    for (const group of groups) {
      const grouped = tiles.filter(t => t.attribute.group === group.id);
      if (grouped.length > 0) sections.push({ id: group.id, name: group.name, label: group.label, tiles: grouped });
    }
    return sections;
  });

  /** When the server read the values, in the viewer's locale. */
  protected readonly generatedAt = computed(() => {
    const marker = this.context().item.metadata?.[SPARK_DASHBOARD_METADATA_KEY];
    const at = marker && typeof marker === 'object' ? (marker as Record<string, unknown>)['generatedAtUtc'] : null;
    if (typeof at !== 'string') return null;
    const date = new Date(at);
    return Number.isNaN(date.getTime()) ? null : date.toLocaleTimeString(this.locale);
  });

  private tile(attribute: EntityAttributeDefinition, item: PersistentObject, formData: Record<string, any>): DashboardTile {
    const value = rendererValue(item.attributes.find(a => a.name === attribute.name));
    const registration = attribute.renderer ? this.renderers.find(r => r.name === attribute.renderer) : undefined;
    const component = registration?.detailComponent ?? null;
    const span = attribute.renderer === 'spark-dashboard-table' || (attribute.columnSpan ?? 1) >= 3 ? 'row' : attribute.columnSpan === 2 ? 2 : 1;
    return {
      attribute,
      span,
      component,
      inputs: component
        ? withDeclaredInputs(component, { value, attribute, options: attribute.rendererOptions, formData, item })
        : {},
      text: formatSparkDashboardValue(value, undefined, this.locale),
    };
  }
}
