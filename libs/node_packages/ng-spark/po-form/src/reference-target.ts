import { EntityType } from '@mintplayer/ng-spark/models';
import { SparkService } from '@mintplayer/ng-spark/services';

/**
 * The entity type a Reference attribute points at, resolved as the server's `GetReferenceTarget`
 * does (reference add-new PRD D9): through its lookup `query`, by name, whose rows are what the picker
 * offers, falling back to `referenceType` (a CLR name). A type with no C# class has no `clrType`, so a
 * lookup by `referenceType` alone would never find it (R10).
 */
export async function resolveReferenceTarget(
  sparkService: SparkService,
  query: string | null | undefined,
  referenceType: string | null | undefined,
): Promise<EntityType | null> {
  const types = await sparkService.getEntityTypes();
  if (query) {
    const rowType = (await sparkService.getQueryByName(query))?.entityType?.toLowerCase();
    const byQuery = rowType ? types.find(t => t.name.toLowerCase() === rowType) : undefined;
    if (byQuery) return byQuery;
  }
  return (referenceType && types.find(t => t.clrType === referenceType)) || null;
}
