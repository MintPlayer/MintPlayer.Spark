import { isReservedAsDetailKey } from './as-detail-conversions';
import { PersistentObject } from './persistent-object';
import { PersistentObjectAttribute } from './persistent-object-attribute';

/**
 * The one answer to "do these two reads hold the same content?", shared by History's revision diff
 * and po-edit's three-way conflict merge.
 *
 * Two reads of identical content are NOT byte-identical: their envelopes carry per-read fields
 * (etags, breadcrumbs, row keys, the wire values a flattened row keeps for its date cells) that
 * differ between reads, and comparing them reports every AsDetail attribute as changed. Both
 * functions below reduce a value to its content first. They come in two shapes because the two
 * callers hold two shapes: History compares wire attributes, po-edit compares the flat form dict.
 */

/**
 * A comparable form of a wire attribute's content.
 *
 * Nested objects (AsDetail rows, references rendered as objects) are reduced to their attributes'
 * values: their envelopes carry per-read fields (etags, breadcrumbs, row keys) that differ between two
 * reads of identical content and would report every AsDetail attribute as changed.
 */
export function comparableValue(attribute: PersistentObjectAttribute | undefined): string {
  if (!attribute) return '\u0000absent';
  return JSON.stringify({
    v: attribute.value ?? null,
    o: attribute.object ? nestedValues(attribute.object) : null,
    os: attribute.objects ? attribute.objects.map(nestedValues) : null,
  });
}

function nestedValues(po: PersistentObject): Record<string, string> {
  const result: Record<string, string> = {};
  for (const a of po.attributes ?? []) result[a.name] = comparableValue(a);
  return result;
}

/**
 * A comparable form of a value in the flat form dict ({@link nestedPoToDict}'s shape).
 *
 * The reserved keys a flattened row carries (its row key, its breadcrumb, the wire values of its
 * dates) are dropped, and object keys are sorted, so two flattenings of the same content compare
 * equal whatever order their attributes arrived in. `undefined` and `null` are the same absence.
 */
export function comparableFormValue(value: unknown): string {
  return JSON.stringify(normalizeFormValue(value));
}

/** Whether two form-dict values hold the same content (see {@link comparableFormValue}). */
export function formValuesEqual(a: unknown, b: unknown): boolean {
  return comparableFormValue(a) === comparableFormValue(b);
}

function normalizeFormValue(value: unknown): unknown {
  if (value === undefined || value === null) return null;
  if (Array.isArray(value)) return value.map(normalizeFormValue);
  if (typeof value === 'object') {
    const result: Record<string, unknown> = {};
    for (const key of Object.keys(value as object).sort()) {
      if (isReservedAsDetailKey(key)) continue;
      result[key] = normalizeFormValue((value as Record<string, unknown>)[key]);
    }
    return result;
  }
  return value;
}
