import { EntityAttributeDefinition } from './entity-type';
import { EntityType } from './entity-type';
import { PersistentObject } from './persistent-object';
import { PersistentObjectAttribute } from './persistent-object-attribute';

/**
 * Resolves an `EntityType` by its CLR type name (e.g. `"HR.Entities.Address"`).
 * Callers typically close over a list they already hold. `getEntityTypes()` is NOT cached --
 * it issues a request per call -- so resolve against a list you fetched once rather than
 * calling it inside the resolver.
 */
export type EntityTypeResolver = (clrTypeName: string) => EntityType | undefined;

/**
 * Flattens a nested `PersistentObject` into the plain `Record<string, any>` shape the
 * form state uses throughout ng-spark. Primitive / reference attributes contribute their
 * `value`; nested AsDetail attributes recurse — single becomes an inner dict, array
 * becomes an array of inner dicts. Returns `{}` for `null` / `undefined` input.
 *
 * This is the ONE place that reads the server's new AsDetail wire shape and collapses it
 * back to the flat dict the form components already handle.
 */
export function nestedPoToDict(po: PersistentObject | null | undefined): Record<string, any> {
  if (!po) return {};
  const dict: Record<string, any> = {};
  for (const attr of po.attributes ?? []) {
    dict[attr.name] = attributeValueForForm(attr);
  }
  // The object's own server-resolved breadcrumb, kept under a reserved key so the form can label
  // it without re-deriving a template it may be structurally unable to resolve. Safe to carry
  // through save: `dictToNestedPo` walks the entity type's attributes, never the dict's keys, so
  // a reserved key is never sent to the server. Attached only when it resolved, which keeps the
  // common case byte-for-byte identical to the plain flat dict.
  if (typeof po.breadcrumb === 'string' && po.breadcrumb !== '') {
    dict[AS_DETAIL_SELF_BREADCRUMB_KEY] = po.breadcrumb;
  }
  // The row's own key, same reserved-key mechanism and for a sharper reason: without it the key
  // never returns, and a save cannot tell an edited row from a deleted one plus a new one. See
  // AS_DETAIL_ROW_KEY.
  if (typeof po.id === 'string' && po.id !== '') {
    dict[AS_DETAIL_ROW_KEY] = po.id;
  }
  return dict;
}

function attributeValueForForm(attr: PersistentObjectAttribute): any {
  if (attr.dataType === 'AsDetail') {
    if (attr.isArray) return (attr.objects ?? []).map(po => nestedPoToDict(po));
    return attr.object ? nestedPoToDict(attr.object) : null;
  }
  return attr.value;
}

/**
 * Reserved key under which a flattened nested object keeps the breadcrumb the SERVER resolved for
 * that object itself (as opposed to {@link AS_DETAIL_BREADCRUMBS_KEY}, which keys the breadcrumbs
 * of its reference attributes by attribute name).
 *
 * This exists because a breadcrumb template can name a property the model does not carry. HR's
 * `Address` declares `[Breadcrumb, IgnoreProperty] string Crumb`, and `Address.json` renders it as
 * `"{Crumb}"` — the server resolves that by reflecting over the CLR property, which no client can
 * do, because `[IgnoreProperty]` is exactly the instruction to keep it out of the model. Flattening
 * used to discard the resolved string, leaving the form to substitute `{Crumb}` against a dict that
 * can never contain it.
 */
export const AS_DETAIL_SELF_BREADCRUMB_KEY = '__sparkBreadcrumb';

/**
 * Reserved key under which a flattened nested object keeps its own row key — the value of whatever
 * property the server registered as that type's `[ValueKey]`.
 *
 * A value object's key is not a model attribute (nothing declares `Id` in its model file), so it
 * travels as the nested `PersistentObject`'s `id` and has to be carried across the flat-dict form
 * state by hand. Before this existed, flattening dropped it and `dictToNestedPo` rebuilt `id` from
 * `dict['Id']`, which is never present — so **every** embedded row reached the server with an empty
 * id, and the fresh instance the mapper builds kept the guid its own field initializer had just
 * minted.
 *
 * That is not cosmetic. Stored keys and incoming keys were then both plausible and never equal, so
 * a save could not match rows: an edit was indistinguishable from a delete plus a create. Every
 * per-row rule — preserving read-only fields, and the `New`/`Edit`/`Delete` rights of the row type —
 * rests on this key surviving the round trip.
 *
 * Safe to carry, for the same reason the breadcrumb is: `dictToNestedPo` walks the entity type's
 * attributes and never the dict's keys, so a reserved key is never sent back as an attribute.
 */
export const AS_DETAIL_ROW_KEY = '__sparkRowKey';

/**
 * The breadcrumb the server resolved for a flattened object, or null when it resolved to nothing.
 *
 * `EntityMapper` never emits an empty breadcrumb: when the template renders blank it substitutes
 * the CLR type name, so an unset `Address` arrives as the literal string `"Address"`
 * (EntityMapper.cs:209-211). That is a placeholder, not data, and rendering it would be worse than
 * rendering nothing — it reads as a real value. `typeName` lets a caller filter it back out.
 */
export function selfBreadcrumb(row: Record<string, any> | null | undefined, typeName?: string): string | null {
  return resolvedBreadcrumb(row?.[AS_DETAIL_SELF_BREADCRUMB_KEY], typeName);
}

/**
 * The same filter as {@link selfBreadcrumb}, for a caller holding the server's breadcrumb string
 * directly rather than a flattened row dict — `attr.object.breadcrumb` on a detail page, or a query
 * cell's `breadcrumb`.
 *
 * Split out so there is exactly one answer to "is this the type-name placeholder?". The first
 * implementation lived inside `selfBreadcrumb` and so covered only the two pipes that flatten a row
 * first; the two that read the string straight off the wire kept printing `BuildFeedback` and
 * `GateSettings` at users (#384). A second copy of the comparison is how that happens again.
 */
export function resolvedBreadcrumb(value: unknown, typeName?: string): string | null {
  if (typeof value !== 'string' || value.trim() === '') return null;

  // Callers hold the type name in two shapes — `EntityType.name` is the short model name, while an
  // attribute's `asDetailType` is the full CLR name — and the server's placeholder is always the
  // short one. Compare on the last dotted segment so either shape filters it.
  if (typeName) {
    const shortName = typeName.slice(typeName.lastIndexOf('.') + 1);
    if (value === shortName) return null;
  }
  return value;
}

/**
 * Reserved key under which {@link nestedPoToDisplayRow} stashes the server-resolved breadcrumb of
 * each reference attribute (keyed by attribute name). Lets an AsDetail reference cell render the
 * label the server already resolved by id — page-independent — instead of guessing from a single
 * reference-query options page. Prefixed to avoid colliding with a real attribute name.
 */
export const AS_DETAIL_BREADCRUMBS_KEY = '__sparkBreadcrumbs';

/**
 * Whether a flattened-row key is one this module reserved rather than a model attribute.
 *
 * `nestedPoToDict` and `nestedPoToDisplayRow` stash the row key and the resolved breadcrumbs
 * alongside the real values, which is safe on the way back — `dictToNestedPo` walks the entity
 * type's attributes and never the dict's keys. It is *not* safe for anything that enumerates the
 * dict to build display text: a caller joining `Object.values(dict)` prints the row's guid and its
 * own breadcrumb as though they were fields.
 */
export function isReservedAsDetailKey(key: string): boolean {
  return key === AS_DETAIL_ROW_KEY || key === AS_DETAIL_SELF_BREADCRUMB_KEY || key === AS_DETAIL_BREADCRUMBS_KEY;
}

/**
 * Like {@link nestedPoToDict}, but for the read-only detail display path. In addition to each
 * attribute's value it preserves the server-resolved per-reference `breadcrumb` under
 * {@link AS_DETAIL_BREADCRUMBS_KEY}, so an AsDetail reference cell can render the label by id
 * regardless of whether the referenced document fits on the reference query's first options page.
 * The form/edit path keeps using {@link nestedPoToDict}, which never carries breadcrumbs.
 */
export function nestedPoToDisplayRow(po: PersistentObject | null | undefined): Record<string, any> {
  if (!po) return {};
  const dict: Record<string, any> = {};
  let breadcrumbs: Record<string, string> | undefined;
  for (const attr of po.attributes ?? []) {
    dict[attr.name] = displayValueForAttribute(attr);
    if (attr.dataType === 'Reference' && !attr.isArray && typeof attr.breadcrumb === 'string' && attr.breadcrumb !== '') {
      (breadcrumbs ??= {})[attr.name] = attr.breadcrumb;
    }
  }
  // Only attach the side channel when something resolved — keeps reference-free rows (the common
  // case) byte-for-byte identical to the plain flat dict.
  if (breadcrumbs) dict[AS_DETAIL_BREADCRUMBS_KEY] = breadcrumbs;
  // Same self-breadcrumb the form path carries: a nested-AsDetail COLUMN in a detail table has an
  // inner dict as its value, which used to stringify to "[object Object]".
  if (typeof po.breadcrumb === 'string' && po.breadcrumb !== '') {
    dict[AS_DETAIL_SELF_BREADCRUMB_KEY] = po.breadcrumb;
  }
  // And the row key, for the same reason as the form path — a display row can be handed back for
  // saving, and a row that loses its key on that trip is indistinguishable from a new one.
  if (typeof po.id === 'string' && po.id !== '') {
    dict[AS_DETAIL_ROW_KEY] = po.id;
  }
  return dict;
}

function displayValueForAttribute(attr: PersistentObjectAttribute): any {
  if (attr.dataType === 'AsDetail') {
    if (attr.isArray) return (attr.objects ?? []).map(po => nestedPoToDisplayRow(po));
    return attr.object ? nestedPoToDisplayRow(attr.object) : null;
  }
  return attr.value;
}

/**
 * Builds a nested `PersistentObject` from a flat dict against the schema in
 * <paramref name="entityType"/>. Used when the form is about to save — AsDetail attributes
 * are no longer sent as flat dicts in `attribute.value`; the server now requires
 * `attribute.object` / `attribute.objects` with fully scaffolded nested POs.
 *
 * `resolve` walks through AsDetail types registered elsewhere (usually the full
 * `getEntityTypes()` list, keyed by CLR type name). Nested AsDetail inside AsDetail is
 * handled recursively.
 */
export function dictToNestedPo(
  dict: Record<string, any> | null | undefined,
  entityType: EntityType,
  resolve: EntityTypeResolver,
): PersistentObject {
  const attributes: PersistentObjectAttribute[] = (entityType.attributes ?? [])
    .map(attrDef => buildAttribute(attrDef, dict?.[attrDef.name], resolve));

  return {
    // The reserved key first: it is the only one that is always present for a stored row, because
    // a value object's key is not a model attribute. `Id`/`id` remain as fallbacks for a root
    // object, whose id genuinely is part of the dict.
    id: (dict?.[AS_DETAIL_ROW_KEY] as string) ?? (dict?.['Id'] as string) ?? (dict?.['id'] as string) ?? '',
    name: entityType.name,
    objectTypeId: entityType.id,
    attributes,
  };
}

function buildAttribute(
  attrDef: EntityAttributeDefinition,
  raw: any,
  resolve: EntityTypeResolver,
): PersistentObjectAttribute {
  const attr: PersistentObjectAttribute = {
    id: attrDef.id,
    name: attrDef.name,
    label: attrDef.label,
    dataType: attrDef.dataType,
    isArray: attrDef.isArray,
    isRequired: attrDef.isRequired,
    isVisible: attrDef.isVisible,
    isReadOnly: attrDef.isReadOnly,
    order: attrDef.order,
    rules: attrDef.rules ?? [],
    isValueChanged: true,
  };

  if (attrDef.dataType === 'AsDetail') {
    // Server expects attr.value null for AsDetail; the nested PO carries the data.
    attr.value = null;
    attr.asDetailType = attrDef.asDetailType;

    const nestedType = attrDef.asDetailType ? resolve(attrDef.asDetailType) : undefined;
    if (!nestedType) {
      attr.object = null;
      attr.objects = attrDef.isArray ? [] : null;
      return attr;
    }

    if (attrDef.isArray) {
      const items: any[] = Array.isArray(raw) ? raw : [];
      attr.objects = items.map(item => dictToNestedPo((item as Record<string, any>) ?? {}, nestedType, resolve));
    } else {
      attr.object = raw ? dictToNestedPo(raw as Record<string, any>, nestedType, resolve) : null;
    }
    return attr;
  }

  attr.value = raw;
  return attr;
}
