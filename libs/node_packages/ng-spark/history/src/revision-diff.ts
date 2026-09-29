import { EntityAttributeDefinition, EntityType, PersistentObject, PersistentObjectAttribute, ShowedOn, hasShowedOnFlag } from '@mintplayer/ng-spark/models';

/** One attribute that differs between a revision and the current object. */
export interface SparkRevisionChange {
  attribute: EntityAttributeDefinition;
  /** Absent when the attribute did not exist on that side (added or removed since). */
  revision: PersistentObjectAttribute | undefined;
  current: PersistentObjectAttribute | undefined;
}

/**
 * The attributes a revision is compared on: the ones the detail page shows, in its order. Redacted
 * attributes are simply absent from what the server sends, so they never appear as a difference.
 */
export function revisionAttributes(entityType: EntityType | null | undefined): EntityAttributeDefinition[] {
  return (entityType?.attributes ?? [])
    .filter(a => a.isVisible && hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
    .sort((a, b) => a.order - b.order);
}

/**
 * A comparable form of an attribute's content.
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

/** The shown attributes whose content differs between `revision` and `current`, in display order. */
export function diffRevision(
  entityType: EntityType | null | undefined,
  revision: PersistentObject | null | undefined,
  current: PersistentObject | null | undefined,
): SparkRevisionChange[] {
  if (!revision || !current) return [];
  const changes: SparkRevisionChange[] = [];
  for (const attribute of revisionAttributes(entityType)) {
    const r = revision.attributes.find(a => a.name === attribute.name);
    const c = current.attributes.find(a => a.name === attribute.name);
    if (comparableValue(r) !== comparableValue(c)) changes.push({ attribute, revision: r, current: c });
  }
  return changes;
}
