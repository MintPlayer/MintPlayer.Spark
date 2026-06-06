import { EntityAttributeDefinition } from './entity-type';
import { EntityType } from './entity-type';
import { PersistentObject } from './persistent-object';
import { PersistentObjectAttribute } from './persistent-object-attribute';

/**
 * Resolves an `EntityType` by its CLR type name (e.g. `"HR.Entities.Address"`).
 * Callers typically close over `sparkService.getEntityTypes()`'s cached list.
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
    id: (dict?.['Id'] as string) ?? (dict?.['id'] as string) ?? '',
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
