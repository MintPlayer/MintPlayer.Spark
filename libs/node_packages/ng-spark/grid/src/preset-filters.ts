import { EntityType, hasShowedOnFlag, QueryColumnFilter, ShowedOn } from '@mintplayer/ng-spark/models';

const NUMERIC_TYPES = new Set(['number', 'int', 'integer', 'long', 'decimal', 'double', 'float']);

/**
 * The preset column filters (a query page's URL filters) that name an attribute of the query's
 * entity type, with their values coerced to the attribute's type — a URL carries only strings, and
 * the server compares a number column against numbers. Filters naming anything else are dropped, so
 * an unrelated query-string parameter never becomes a request.
 */
export function applicablePresetFilters(
  filters: readonly QueryColumnFilter[] | null | undefined,
  entityType: EntityType | null | undefined,
): QueryColumnFilter[] {
  if (!filters?.length || !entityType) return [];
  const result: QueryColumnFilter[] = [];
  for (const filter of filters) {
    const attribute = (entityType.attributes ?? []).find(a => a.name === filter.name);
    // Only a grid column can be filtered; the server refuses the rest anyway.
    if (!attribute || !hasShowedOnFlag(attribute.showedOn, ShowedOn.Query)) continue;
    const coerce = (value: unknown): unknown => {
      if (typeof value !== 'string') return value;
      if (NUMERIC_TYPES.has(attribute.dataType)) {
        const n = Number(value);
        return value.trim() !== '' && Number.isFinite(n) ? n : value;
      }
      if (attribute.dataType === 'boolean') return value === 'true' ? true : value === 'false' ? false : value;
      return value;
    };
    const includes = filter.includes?.map(coerce);
    const excludes = filter.excludes?.map(coerce);
    if (!includes?.length && !excludes?.length) continue;
    result.push({ name: filter.name, ...(includes?.length ? { includes } : {}), ...(excludes?.length ? { excludes } : {}) });
  }
  return result;
}
