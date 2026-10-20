import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { EntityAttributeDefinition, PersistentObject } from '@mintplayer/ng-spark/models';
import {
  SparkAttributeColumnRenderer,
  SparkAttributeDetailRenderer,
  SparkAttributeEditRenderer,
  SparkAttributeRendererRegistration,
} from '@mintplayer/ng-spark/renderers';
import { SparkAddress, SparkAddressInputComponent, renderAddressLines } from '@mintplayer/ng-spark/reference-data';

/** The renderer name a model attribute carries: `"renderer": "spark-address"` on a single AsDetail `Address`. */
export const SPARK_ADDRESS_RENDERER = 'spark-address';

/**
 * An address as a renderer receives it: the flat record of the edit form, the nested PersistentObject of the
 * detail page and of AsDetail tables, or the projected value of a query grid.
 */
export function asSparkAddress(value: unknown): SparkAddress | null {
  if (!value || typeof value !== 'object') return null;
  const po = value as PersistentObject;
  if (Array.isArray(po.attributes))
    return Object.fromEntries(po.attributes.map(a => [a.name, a.value])) as SparkAddress;
  return value as SparkAddress;
}

function linesOf(value: unknown): string[] {
  const address = asSparkAddress(value);
  if (!address) return [];
  return Array.isArray(address.Lines) && address.Lines.length > 0 ? address.Lines : renderAddressLines(address);
}

/** The edit form: the whole address object edited by `<spark-address-input>` (X15: a single AsDetail with a renderer). */
@Component({
  selector: 'spark-address-edit-renderer',
  imports: [SparkAddressInputComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<spark-address-input [value]="address()" (valueChange)="valueChange()($event)" />`,
})
export class SparkAddressEditRendererComponent implements SparkAttributeEditRenderer {
  readonly value = input<any>();
  readonly attribute = input<EntityAttributeDefinition | undefined>();
  readonly options = input<Record<string, any> | undefined>();
  readonly valueChange = input<(value: any) => void>(() => {});

  protected readonly address = computed(() => asSparkAddress(this.value()));
}

/** The detail page: the rendered lines, flagged when the address was not checked against reference data. */
@Component({
  selector: 'spark-address-detail-renderer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './spark-address-detail-renderer.component.html',
  styleUrl: './spark-address-detail-renderer.component.scss',
})
export class SparkAddressDetailRendererComponent implements SparkAttributeDetailRenderer {
  readonly value = input<any>();

  protected readonly lines = computed(() => linesOf(this.value()));
  protected readonly unverified = computed(() => {
    const address = asSparkAddress(this.value());
    return !!address && address.Verified !== true;
  });
  protected readonly timeZone = computed(() => asSparkAddress(this.value())?.TimeZone ?? null);
}

/** A grid cell: the lines on one row. */
@Component({
  selector: 'spark-address-column-renderer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `{{ text() }}`,
})
export class SparkAddressColumnRendererComponent implements SparkAttributeColumnRenderer {
  readonly value = input<any>();

  protected readonly text = computed(() => {
    const value = this.value();
    return typeof value === 'string' ? value : linesOf(value).join(', ');
  });
}

/**
 * The address renderers to spread into an app's list (PRD §4.15):
 * `provideSparkAttributeRenderers([...sparkAddressRenderers])`, with `provideReferenceData({ baseUrl })`.
 */
export const sparkAddressRenderers: SparkAttributeRendererRegistration[] = [
  {
    name: SPARK_ADDRESS_RENDERER,
    editComponent: SparkAddressEditRendererComponent,
    detailComponent: SparkAddressDetailRendererComponent,
    columnComponent: SparkAddressColumnRendererComponent,
    filterLabel: value => linesOf(value).join(', '),
  },
];
