/** The attribute facts {@link isPrimitiveArrayAttribute} reads. */
export interface PrimitiveArrayCandidate {
  dataType: string;
  isArray?: boolean;
  lookupReferenceType?: string | null;
}

/**
 * Whether an attribute is a list of plain values — a C# `List<string>`, `int[]`, ... — that the form
 * edits one item per input and the detail page shows one chip per item.
 *
 * Not every `isArray` attribute is one. `AsDetail` arrays are rows of embedded objects with their own
 * editor, and `Reference` arrays carry ids picked through the multi-reference tree select. Excluded as
 * well, because their single-value control does not carry over to a list as-is:
 * - a lookup-backed array: one select per item is a different editor (pick from a set), not built;
 * - `boolean`: a list of checkboxes has no meaningful "add an item";
 * - `date` / `datetime`: the control needs a wire ⇄ wall-clock conversion per item, which the
 *   save paths do for a scalar only. Rendered through the list editor, the stored instants would
 *   show blank and be written back blank.
 */
export function isPrimitiveArrayAttribute(attr: PrimitiveArrayCandidate | null | undefined): boolean {
  if (!attr?.isArray || attr.lookupReferenceType) return false;
  switch (attr.dataType) {
    case 'AsDetail':
    case 'Reference':
    case 'boolean':
    case 'date':
    case 'datetime':
      return false;
    default:
      return true;
  }
}
