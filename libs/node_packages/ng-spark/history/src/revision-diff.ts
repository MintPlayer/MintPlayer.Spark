import { EntityAttributeDefinition, EntityType, PersistentObject, PersistentObjectAttribute, ShowedOn, comparableValue, hasShowedOnFlag } from '@mintplayer/ng-spark/models';

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
    .filter(a => hasShowedOnFlag(a.showedOn, ShowedOn.PersistentObject))
    .sort((a, b) => a.order - b.order);
}

// The normalizer lives in models since po-edit's conflict merge needs the same answer; re-exported
// here so History's public surface is unchanged.
export { comparableValue };

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
