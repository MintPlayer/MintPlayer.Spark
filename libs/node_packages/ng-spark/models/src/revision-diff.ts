import { comparableValue } from './comparable-value';
import { EntityAttributeDefinition, EntityType } from './entity-type';
import { PersistentObject } from './persistent-object';
import { PersistentObjectAttribute } from './persistent-object-attribute';
import { ShowedOn, hasShowedOnFlag } from './showed-on';

/**
 * One attribute that differs between two reads of an object: a revision and the current object
 * (History), or the current object and a proposed one (Approvals).
 */
export interface SparkRevisionChange {
  attribute: EntityAttributeDefinition;
  /** The left-hand side. Absent when the attribute did not exist there (added or removed since). */
  revision: PersistentObjectAttribute | undefined;
  /** The right-hand side. */
  current: PersistentObjectAttribute | undefined;
}

/**
 * The attributes two reads are compared on: the ones the detail page shows, in its order. Redacted
 * attributes are simply absent from what the server sends, so they never appear as a difference.
 */
export function revisionAttributes(entityType: EntityType | null | undefined): EntityAttributeDefinition[] {
  return (entityType?.attributes ?? [])
    .filter(a => hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
    .sort((a, b) => a.order - b.order);
}

/**
 * The shown attributes whose content differs between `revision` and `current`, in display order.
 *
 * Lives in models (it moved here from History) so that every add-on comparing two reads of an object,
 * Approvals' proposal diff among them, shares one answer without depending on History.
 */
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
