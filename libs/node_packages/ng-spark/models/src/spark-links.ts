import { EntityType } from './entity-type';

/**
 * An object's page when the server sent no `href` (#602): a reference, a program unit, a board tile, a navigate
 * operation. The type's `route` template without the slug, else `/po/{alias}/{id}`. The page then puts the canonical
 * `href` in the address bar itself ({@link SparkObjectUrlService}), and a server render answers it with a 301.
 * @param type The entity type, or its alias or id when only that is known (no template is applied then).
 */
export function sparkDetailLink(type: EntityType | string, id: string): string {
  const segment = typeof type === 'string' ? type : type.alias || type.id;
  const template = typeof type === 'string' ? undefined : type.route?.trim();
  if (!template) return `/po/${encodeURIComponent(segment)}/${encodeURIComponent(id)}`;

  const parts = template.replace(/^\/+|\/+$/g, '').split('/').filter(p => p.length > 0 && p !== '{slug}');
  return '/' + parts
    .map(p => p === '{id}' ? encodeURIComponent(id) : p === '{type}' ? encodeURIComponent(segment) : p)
    .join('/');
}

/** The object's page: the server's `href` when it sent one, else {@link sparkDetailLink}. */
export function sparkObjectLink(item: { href?: string | null } | null | undefined, type: EntityType | string, id: string): string {
  return item?.href || sparkDetailLink(type, id);
}
