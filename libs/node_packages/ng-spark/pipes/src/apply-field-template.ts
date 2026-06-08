/**
 * Fills a `{Field}` template from a plain object's own fields. Used ONLY for AsDetail
 * nested-object summary display: an embedded value object (no id, no document) is rendered
 * client-side from its own fields. This is deliberately distinct from entity/reference
 * breadcrumbs — those are resolved recursively on the server (BreadcrumbResolver) and the
 * client only reads the resulting strings. Unknown or null placeholders render empty.
 */
export function applyFieldTemplate(template: string, data: Record<string, any>): string {
  return template.replace(/\{(\w+)\}/g, (_match, propertyName) => {
    const value = data[propertyName];
    return value != null ? String(value) : '';
  });
}
