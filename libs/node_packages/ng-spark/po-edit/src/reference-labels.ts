import { PersistentObject, QueryResultItem } from '@mintplayer/ng-spark/models';

/**
 * Reference labels by referenced id, for rendering a Reference in the conflict dialog.
 *
 * The form keeps only a reference's id (`nestedPoToDict` drops the breadcrumbs), so a conflict value
 * on its own renders as the raw id. The labels live elsewhere: on the objects as read — the object as
 * loaded and the re-fetched one each carry the server-resolved breadcrumb of every reference they
 * hold, rows included — and, for a reference the user just picked, in the candidate list the picker
 * chose it from (a picker only ever selects from those options). Ids are document ids, unique across
 * collections, so one flat map serves every attribute and every row.
 */
export type ReferenceLabels = Record<string, string>;

/** Adds the labels of every reference in `po` (recursing into AsDetail rows). The first label for an id wins. */
export function addReferenceLabelsOf(po: PersistentObject | null | undefined, into: ReferenceLabels): ReferenceLabels {
  for (const attr of po?.attributes ?? []) {
    if (attr.object) addReferenceLabelsOf(attr.object, into);
    for (const row of attr.objects ?? []) addReferenceLabelsOf(row, into);
    for (const [id, label] of Object.entries(attr.breadcrumbs ?? {})) add(into, id, label);
    if (typeof attr.value === 'string') add(into, attr.value, attr.breadcrumb);
  }
  return into;
}

/** Adds the labels of reference candidates (`QueryResultItem.breadcrumb` by `id`). The first label for an id wins. */
export function addReferenceLabelsOfOptions(options: Iterable<readonly QueryResultItem[] | undefined>, into: ReferenceLabels): ReferenceLabels {
  for (const list of options) {
    for (const item of list ?? []) {
      if (item.id) add(into, item.id, item.breadcrumb);
    }
  }
  return into;
}

function add(into: ReferenceLabels, id: string, label: string | null | undefined): void {
  if (id === '' || id in into || typeof label !== 'string' || label.trim() === '') return;
  into[id] = label;
}
