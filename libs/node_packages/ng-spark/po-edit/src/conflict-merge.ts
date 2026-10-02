import {
  AS_DETAIL_ROW_KEY,
  EntityAttributeDefinition,
  EntityTypeResolver,
  formValuesEqual,
  rowMetadata,
  selfBreadcrumb,
} from '@mintplayer/ng-spark/models';

/**
 * Three-way merge of an edit form after a 409 (contributions PRD §5, Q10).
 *
 * The three sides are all in the form's own flat-dict shape (`nestedPoToDict`): **base** is the
 * object as this page loaded it, **mine** is the form now, **theirs** is the object re-fetched after
 * the server refused the save. Comparing in the form's shape rather than on the wire matters: the
 * form coerces while it loads (an unset string becomes `''`, a date becomes a wall clock), so a
 * wire-shaped base would report every such field as "changed by me" and turn an edit the other
 * user made to it into a false conflict. All three sides go through the same flattening, so only
 * real edits differ.
 *
 * Pure: no Angular, no I/O. Values are compared with `formValuesEqual`, which ignores the per-read
 * fields a flattened row carries (row key, breadcrumb, original dates).
 */

/** Which side a conflict is resolved to. */
export type ConflictSide = 'mine' | 'theirs';

export interface MergeSchema {
  /** The attributes being merged: the ones the form edits. */
  attributes: EntityAttributeDefinition[];
  /** Resolves AsDetail row types by CLR name, to merge rows per attribute. */
  resolve: EntityTypeResolver;
}

export interface MergeConflict {
  /**
   * Where the conflict sits, and the key its choice is given under: `Title`, `Address.Street`,
   * `Lyrics[ko/Kore]` for a whole row, `Lyrics[ko/Kore].Text` for one attribute of a row.
   */
  path: string;
  /** `value`: both sides changed one attribute differently. `row`: a row keep/remove or add/add clash. */
  kind: 'value' | 'row';
  /** The attribute the values belong to — for a row conflict, the AsDetail list attribute. */
  attribute: EntityAttributeDefinition;
  /** The top-level attribute this sits under (the attribute itself at the root). */
  rootAttribute: EntityAttributeDefinition;
  /** The path of the object this sits in: `''` at the root, `Lyrics[ko/Kore]` inside a row. */
  group: string;
  /** A readable name for the row this sits in, when it is inside one. */
  rowLabel?: string;
  /**
   * For a row conflict, or a value conflict on an embedded (AsDetail) object: the row type's
   * attributes, so the values can be shown cell by cell. Absent when the row type did not resolve.
   */
  rowAttributes?: EntityAttributeDefinition[];
  /** The row type's name, to filter the server's type-name breadcrumb placeholder. */
  rowTypeName?: string;
  /** The values, in the form's shape. For a row conflict, the row dict, or `undefined` when absent. */
  base: unknown;
  mine: unknown;
  theirs: unknown;
}

/** Something the other side changed that the merge took over. */
export interface TheirChange {
  path: string;
  attribute: EntityAttributeDefinition;
  rootAttribute: EntityAttributeDefinition;
}

export interface MergeResult {
  /** The form values to continue editing with. Unresolved conflicts hold THEIR value. */
  merged: Record<string, any>;
  /** Only true conflicts: both sides changed the same thing to different content. */
  conflicts: MergeConflict[];
  /** What the other side changed that was taken over without a conflict. */
  theirChanges: TheirChange[];
  /**
   * Contribution rows (a `[Contribution]` property, contributions M5b) that I changed but that another
   * contributor changed too: theirs won, my edit of them was dropped — say so.
   */
  contributionNotices: ContributionNotice[];
}

/** A contribution row taken from theirs over my own edit, because another contributor wrote it. */
export interface ContributionNotice {
  /** `Lyrics[en/Latn]`. */
  path: string;
  /** The list attribute. */
  attribute: EntityAttributeDefinition;
  /** A readable name for the row (its breadcrumb, else its key). */
  rowLabel: string;
}

/** The key under which the server marks a contribution row (`metadata.contribution`). */
export const CONTRIBUTION_ROW_METADATA = 'contribution';

/**
 * Whether a flattened row is a contribution row whose shown version the caller wrote: `true` / `false`
 * for a contribution row, `undefined` for any other row (the server marks contribution rows on load).
 */
export function contributionRowIsOwn(row: Record<string, any> | undefined): boolean | undefined {
  const facts = rowMetadata<{ own?: unknown }>(row, CONTRIBUTION_ROW_METADATA);
  return facts && typeof facts.own === 'boolean' ? facts.own : undefined;
}

/**
 * Merges `mine` onto `theirs` against `base`.
 *
 * - An attribute changed only by me keeps mine; changed only by them takes theirs; changed by both
 *   to the same content is no conflict; changed by both differently is a conflict.
 * - A read-only attribute always takes theirs, silently: the user cannot have edited it.
 * - AsDetail lists are matched per row by the row key (`[ValueKey]`). A row added, removed or changed
 *   on one side only comes from that side. The same key added on both sides with different content,
 *   or a row removed on one side and edited on the other, is a row conflict. A row changed on both
 *   sides is merged per attribute. The order is theirs, then the rows only I have.
 *
 * `choices` resolves conflicts by path; a conflict without a choice resolves to theirs. Run it once
 * without choices to find the conflicts, and again with the user's choices to build the result —
 * the conflicts reported do not depend on the choices.
 */
export function mergeThreeWay(
  base: Record<string, any>,
  mine: Record<string, any>,
  theirs: Record<string, any>,
  schema: MergeSchema,
  choices: Readonly<Record<string, ConflictSide>> = {},
): MergeResult {
  const ctx: MergeContext = { resolve: schema.resolve, choices, conflicts: [], theirChanges: [], contributionNotices: [] };
  const merged = mergeObject(schema.attributes, base ?? {}, mine ?? {}, theirs ?? {}, '', undefined, undefined, ctx);
  return { merged, conflicts: ctx.conflicts, theirChanges: ctx.theirChanges, contributionNotices: ctx.contributionNotices };
}

interface MergeContext {
  resolve: EntityTypeResolver;
  choices: Readonly<Record<string, ConflictSide>>;
  conflicts: MergeConflict[];
  theirChanges: TheirChange[];
  contributionNotices: ContributionNotice[];
}

function mergeObject(
  attributes: EntityAttributeDefinition[],
  base: Record<string, any>,
  mine: Record<string, any>,
  theirs: Record<string, any>,
  prefix: string,
  root: EntityAttributeDefinition | undefined,
  rowLabel: string | undefined,
  ctx: MergeContext,
): Record<string, any> {
  // Theirs is the frame: it carries the fresh per-read fields (row key, breadcrumb, original dates).
  const merged: Record<string, any> = { ...theirs };
  for (const attribute of attributes) {
    const path = prefix ? `${prefix}.${attribute.name}` : attribute.name;
    const at: Location = { path, attribute, rootAttribute: root ?? attribute, group: prefix, rowLabel };
    merged[attribute.name] = mergeAttribute(at, base[attribute.name], mine[attribute.name], theirs[attribute.name], ctx);
  }
  return merged;
}

interface Location {
  path: string;
  attribute: EntityAttributeDefinition;
  rootAttribute: EntityAttributeDefinition;
  group: string;
  rowLabel: string | undefined;
  /** Set where the values are rows or embedded objects of a resolved type. */
  rowType?: { attributes: EntityAttributeDefinition[]; name: string };
}

function mergeAttribute(at: Location, base: unknown, mine: unknown, theirs: unknown, ctx: MergeContext): unknown {
  // The user cannot have changed it, so whatever differs is theirs (or the server's).
  if (at.attribute.isReadOnly) return theirs;

  if (at.attribute.dataType === 'AsDetail' && at.attribute.asDetailType) {
    const rowType = ctx.resolve(at.attribute.asDetailType);
    if (rowType) {
      const typed: Location = { ...at, rowType: { attributes: rowType.attributes ?? [], name: rowType.name } };
      if (at.attribute.isArray) {
        return mergeRows(typed, rowType.attributes ?? [], rowType.name, asRows(base), asRows(mine), asRows(theirs), ctx);
      }
      // A single embedded object changed on both sides merges per attribute, like a row.
      if (isPlainObject(base) && isPlainObject(mine) && isPlainObject(theirs)
        && !formValuesEqual(mine, base) && !formValuesEqual(theirs, base)) {
        return mergeObject(rowType.attributes ?? [], base, mine, theirs, at.path, at.rootAttribute, at.rowLabel, ctx);
      }
      return mergeValue(typed, 'value', base, mine, theirs, ctx);
    }
  }

  return mergeValue(at, 'value', base, mine, theirs, ctx);
}

/** The scalar rule, also used for a whole row or object that only one side touched. */
function mergeValue(at: Location, kind: MergeConflict['kind'], base: unknown, mine: unknown, theirs: unknown, ctx: MergeContext): unknown {
  const mineChanged = !formValuesEqual(mine, base);
  const theirsChanged = !formValuesEqual(theirs, base);
  if (!mineChanged) {
    if (theirsChanged) ctx.theirChanges.push({ path: at.path, attribute: at.attribute, rootAttribute: at.rootAttribute });
    return theirs;
  }
  if (!theirsChanged || formValuesEqual(mine, theirs)) return theirsChanged ? theirs : mine;
  return conflict(at, kind, base, mine, theirs, ctx);
}

function conflict(at: Location, kind: MergeConflict['kind'], base: unknown, mine: unknown, theirs: unknown, ctx: MergeContext): unknown {
  ctx.conflicts.push({
    path: at.path, kind, attribute: at.attribute, rootAttribute: at.rootAttribute,
    group: at.group, rowLabel: at.rowLabel,
    ...(at.rowType ? { rowAttributes: at.rowType.attributes, rowTypeName: at.rowType.name } : {}),
    base, mine, theirs,
  });
  return ctx.choices[at.path] === 'mine' ? mine : theirs;
}

type Row = Record<string, any>;

function mergeRows(
  at: Location,
  rowAttributes: EntityAttributeDefinition[],
  rowTypeName: string,
  base: Row[],
  mine: Row[],
  theirs: Row[],
  ctx: MergeContext,
): Row[] {
  const baseByKey = byKey(base);
  const mineByKey = byKey(mine);
  const merged: Row[] = [];
  const seen = new Set<string>();

  const mergeOne = (key: string, b: Row | undefined, m: Row | undefined, t: Row | undefined) => {
    const rowPath = `${at.path}[${key}]`;
    const label = selfBreadcrumb(t ?? m ?? b, rowTypeName) ?? key;
    const rowAt: Location = { ...at, path: rowPath, rowLabel: label };
    const row = mergeRow(rowAt, rowAttributes, b, m, t, ctx);
    if (row !== undefined) merged.push(row as Row);
  };

  // Their order first.
  for (const t of theirs) {
    const key = rowKey(t);
    if (key === undefined) { merged.push(t); continue; }
    if (seen.has(key)) continue;
    seen.add(key);
    mergeOne(key, baseByKey.get(key), mineByKey.get(key), t);
  }
  // Then the rows only I have: rows I added (a new row has no key until it is saved), and rows they
  // removed that I may keep.
  for (const m of mine) {
    const key = rowKey(m);
    if (key === undefined) { merged.push(m); continue; }
    if (seen.has(key)) continue;
    seen.add(key);
    mergeOne(key, baseByKey.get(key), m, undefined);
  }
  return merged;
}

/** One row by key; `undefined` in or out means the row is absent on that side. */
function mergeRow(
  at: Location,
  rowAttributes: EntityAttributeDefinition[],
  base: Row | undefined,
  mine: Row | undefined,
  theirs: Row | undefined,
  ctx: MergeContext,
): Row | undefined {
  // A contribution row (contributions M5b, PRD Q10) is the caller's own contribution document, not
  // a shared field: when both sides touched it and the version THEY show was written by another
  // contributor, theirs wins with a notice — my save would otherwise silently supersede a version I
  // never saw. Only a second tab of the same user (theirs is my own version) is a true conflict, and
  // falls through to the generic rules below. Their removal of a row is judged by who wrote the
  // version it removed (base).
  const own = contributionRowIsOwn(theirs ?? base);
  if (own === false && !formValuesEqual(mine, base) && !formValuesEqual(theirs, base)) {
    ctx.theirChanges.push({ path: at.path, attribute: at.attribute, rootAttribute: at.rootAttribute });
    ctx.contributionNotices.push({ path: at.path, attribute: at.attribute, rowLabel: at.rowLabel ?? at.path });
    return theirs;
  }

  if (base === undefined) {
    // Added. On both sides with the same key is a clash unless the content is the same.
    if (mine !== undefined && theirs !== undefined) {
      return (formValuesEqual(mine, theirs) ? theirs : conflict(at, 'row', undefined, mine, theirs, ctx)) as Row | undefined;
    }
    if (theirs !== undefined) {
      ctx.theirChanges.push({ path: at.path, attribute: at.attribute, rootAttribute: at.rootAttribute });
      return theirs;
    }
    return mine;
  }

  if (mine === undefined && theirs === undefined) return undefined;
  if (mine === undefined) {
    // I removed it. Their edit of it is a clash; their leaving it alone lets the removal stand.
    if (formValuesEqual(theirs, base)) return undefined;
    return conflict(at, 'row', base, undefined, theirs, ctx) as Row | undefined;
  }
  if (theirs === undefined) {
    // They removed it. My edit of it is a clash; my leaving it alone lets their removal stand.
    if (formValuesEqual(mine, base)) {
      ctx.theirChanges.push({ path: at.path, attribute: at.attribute, rootAttribute: at.rootAttribute });
      return undefined;
    }
    return conflict(at, 'row', base, mine, undefined, ctx) as Row | undefined;
  }

  // Present on all three sides: merge attribute by attribute.
  return mergeObject(rowAttributes, base, mine, theirs, at.path, at.rootAttribute, at.rowLabel, ctx);
}

function rowKey(row: Row | undefined): string | undefined {
  const key = row?.[AS_DETAIL_ROW_KEY];
  return typeof key === 'string' && key !== '' ? key : undefined;
}

function byKey(rows: Row[]): Map<string, Row> {
  const map = new Map<string, Row>();
  for (const row of rows) {
    const key = rowKey(row);
    if (key !== undefined && !map.has(key)) map.set(key, row);
  }
  return map;
}

function asRows(value: unknown): Row[] {
  return Array.isArray(value) ? value.filter(isPlainObject) : [];
}

function isPlainObject(value: unknown): value is Record<string, any> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
