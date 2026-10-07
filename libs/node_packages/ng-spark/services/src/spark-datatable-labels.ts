import { SparkLanguageService } from './spark-language.service';

/** The `<bs-datatable>` strings Spark translates: the column-resize handle and its options dialog. */
export interface SparkDatatableLabels {
  resizeColumn: (column: string) => string;
  resizeColumnHint: string;
  resizeColumnOptions: (column: string) => string;
  narrowerColumn: (column: string) => string;
  widerColumn: (column: string) => string;
  columnWidth: (px: number) => string;
  fitColumn: string;
  resetColumn: string;
}

/**
 * The `[labels]` a Spark `<bs-datatable>` passes so the column-resize handle, its keymap
 * announcement and its resize options dialog (ng-bootstrap 22.22.0 / web-components 2.19.0) speak
 * the viewer's language.
 *
 * The `column` these formatters receive is the column's label: every Spark grid binds
 * `bsDatatableColumnLabel` to the header's translated text (and the row-actions column to
 * "Actions"), so the datatable no longer falls back to the internal name. The keys omitted here
 * keep the web component's English defaults.
 *
 * Every translation is read EAGERLY, so a `computed` that calls this tracks the language and the
 * translation map, and hands the datatable a new object — which re-renders it — when either changes.
 */
export function sparkDatatableLabels(lang: SparkLanguageService): SparkDatatableLabels {
  const column = (key: string) => {
    const template = lang.t(key);
    return (name: string) => template.replace('{column}', name);
  };
  const columnWidth = lang.t('common.columnWidth');
  return {
    resizeColumn: column('common.resizeColumn'),
    resizeColumnHint: lang.t('common.resizeColumnHint'),
    resizeColumnOptions: column('common.resizeColumnOptions'),
    narrowerColumn: column('common.narrowerColumn'),
    widerColumn: column('common.widerColumn'),
    columnWidth: (px: number) => columnWidth.replace('{px}', String(px)),
    fitColumn: lang.t('common.fitColumn'),
    resetColumn: lang.t('common.resetColumn'),
  };
}
