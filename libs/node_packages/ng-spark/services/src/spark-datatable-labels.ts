import { TranslatedString } from '@mintplayer/ng-spark/models';
import { SparkLanguageService } from './spark-language.service';

/**
 * The `[labels]` a Spark `<bs-datatable>` passes so the column-resize handles carry the column's
 * DISPLAYED, translated label in the viewer's language.
 *
 * Why this exists: `*bsDatatableColumn` hands the web component only the column's `name`, so its
 * default `Resize column ${label ?? name}` announced the internal name ("Resize column Created"
 * under a header reading "Added", "Resize column __sparkRowActions"), in English. Mapping the name
 * back to the label here is what Spark controls; the directive itself has no label input.
 *
 * Every translation is read EAGERLY, so a `computed` that calls this tracks the language and the
 * translation map, and hands the datatable a new object — which re-renders it — when either changes.
 *
 * @param columns the grid's data columns: `name` as bound to `*bsDatatableColumn`, `label` as shown.
 * @param extra labels for columns that are not data columns (the row-actions column), by name.
 */
export function sparkDatatableLabels(
  lang: SparkLanguageService,
  columns: readonly { name: string; label?: TranslatedString }[],
  extra: Readonly<Record<string, string>> = {},
): { resizeColumn: (column: string) => string } {
  const template = lang.t('common.resizeColumn');
  const labels = new Map<string, string>(Object.entries(extra));
  for (const column of columns) {
    labels.set(column.name, lang.resolve(column.label) || column.name);
  }
  return {
    resizeColumn: (column: string) => template.replace('{column}', labels.get(column) ?? column),
  };
}
