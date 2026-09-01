import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { CoverageSummary } from '../services/browse.service';

/**
 * Normalizes a CoverageSummary attribute value onto the app's shape. Spark
 * delivers AsDetail values as a nested PersistentObject (since Spark#241);
 * the /api endpoints deliver a flat camelCase dict.
 */
export function toCoverageSummary(value: PersistentObject | Record<string, any> | null | undefined): CoverageSummary | null {
  if (!value) return null;

  const po = value as PersistentObject;
  if (Array.isArray(po.attributes)) {
    const byName = new Map(po.attributes.map((a) => [a.name, a.value]));
    const coverable = Number(byName.get('LinesCoverable') ?? 0);
    if (!coverable && !Number(byName.get('FilesCount') ?? 0)) return null;
    return {
      linesCovered: Number(byName.get('LinesCovered') ?? 0),
      linesCoverable: coverable,
      branchesCovered: Number(byName.get('BranchesCovered') ?? 0),
      branchesTotal: Number(byName.get('BranchesTotal') ?? 0),
      filesCount: Number(byName.get('FilesCount') ?? 0),
    };
  }

  const dict = value as Record<string, any>;
  return 'linesCoverable' in dict ? (dict as CoverageSummary) : null;
}
