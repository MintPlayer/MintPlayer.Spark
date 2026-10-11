import { ChangeDetectionStrategy, Component, computed, inject, input, LOCALE_ID } from '@angular/core';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkIconComponent } from '@mintplayer/ng-spark/icon';
import type { SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { formatSparkDashboardValue, SparkDashboardFormat } from './dashboard-format';

/** One column of a `spark-dashboard-table`. */
export interface SparkDashboardTableColumn {
  /** The row field. For a value that is a dictionary, its rows are `{ key, value }`. */
  key: string;
  /** A translation key for the header (`translations.json`), shown as is when it has no translation. Default `key`. */
  label?: string;
  /** How the cells are written. Numbers and bytes are right-aligned. Default: as is. */
  format?: SparkDashboardFormat | 'boolean';
  /** Translate each cell as `{translate}{value}` (e.g. `'seoUi.runStatus.'`), falling back to the value. */
  translate?: string;
}

/** `rendererOptions` of the `spark-dashboard-table` renderer. */
export interface SparkDashboardTableOptions {
  /** The columns, in order. Default: the first row's fields. */
  columns?: SparkDashboardTableColumn[];
  /** A translation key shown when there are no rows. Default `'dashboards.empty'`. */
  empty?: string;
}

interface TableCell {
  text: string;
  check: boolean;
}

/**
 * The `spark-dashboard-table` renderer (detail pages): a dashboard value that is a list of rows, such as the
 * RavenDB statistics' collections (D12: tables are JSON attributes). A dictionary (`{ "Completed": 3 }`) reads as
 * rows `{ key, value }`.
 */
@Component({
  selector: 'spark-dashboard-table',
  imports: [BsTableComponent, SparkIconComponent],
  template: `
    @if (rows().length > 0) {
      <bs-table [isResponsive]="true" class="d-block mb-0">
        <thead>
          <tr>
            @for (column of columns(); track column.key) {
              <th [class.text-end]="isNumeric(column)">{{ header(column) }}</th>
            }
          </tr>
        </thead>
        <tbody>
          @for (row of cells(); track $index) {
            <tr>
              @for (cell of row; track $index; let i = $index) {
                <td [class.text-end]="isNumeric(columns()[i])" [class.text-break]="!isNumeric(columns()[i])">
                  @if (cell.check) { <spark-icon name="check-lg" class="text-success" aria-hidden="true" /><span class="visually-hidden">{{ cell.text }}</span> } @else { {{ cell.text }} }
                </td>
              }
            </tr>
          }
        </tbody>
      </bs-table>
    } @else {
      <span class="text-body-secondary small" data-empty>{{ lang.t(settings().empty ?? 'dashboards.empty', '—') }}</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SparkDashboardTableComponent implements SparkAttributeDetailRenderer {
  private readonly locale = inject(LOCALE_ID);
  protected readonly lang = inject(SparkLanguageService);

  readonly value = input<any>();
  readonly options = input<Record<string, any> | undefined>();

  protected readonly settings = computed(() => (this.options() ?? {}) as SparkDashboardTableOptions);

  protected readonly rows = computed<Record<string, unknown>[]>(() => toSparkTableRows(this.value()));

  protected readonly columns = computed<SparkDashboardTableColumn[]>(() => {
    const declared = this.settings().columns;
    if (Array.isArray(declared) && declared.length > 0) return declared.filter(c => typeof c?.key === 'string');
    const first = this.rows()[0];
    return first ? Object.keys(first).map(key => ({ key })) : [];
  });

  protected readonly cells = computed<TableCell[][]>(() => {
    const columns = this.columns();
    return this.rows().map(row => columns.map(column => this.cell(row[column.key], column)));
  });

  protected header(column: SparkDashboardTableColumn): string {
    return column.label ? this.lang.t(column.label, column.label) : column.key;
  }

  protected isNumeric(column: SparkDashboardTableColumn | undefined): boolean {
    return column?.format === 'number' || column?.format === 'bytes' || column?.format === 'percent';
  }

  private cell(value: unknown, column: SparkDashboardTableColumn): TableCell {
    if (column.format === 'boolean')
      return { text: value === true ? this.lang.t('dashboards.yes', 'yes') : '', check: value === true };
    if (column.translate && value !== null && value !== undefined && value !== '')
      return { text: this.lang.t(column.translate + String(value), String(value)), check: false };
    return { text: formatSparkDashboardValue(value, column.format, this.locale), check: false };
  }
}

/** The rows of a table value: a list of objects as is, a dictionary as `{ key, value }` rows, anything else none. */
export function toSparkTableRows(value: unknown): Record<string, unknown>[] {
  if (Array.isArray(value)) return value.filter((r): r is Record<string, unknown> => r !== null && typeof r === 'object' && !Array.isArray(r));
  if (value !== null && typeof value === 'object') return Object.entries(value).map(([key, v]) => ({ key, value: v }));
  if (typeof value === 'string' && value.trim().startsWith('[')) {
    try {
      return toSparkTableRows(JSON.parse(value));
    } catch {
      return [];
    }
  }
  return [];
}
