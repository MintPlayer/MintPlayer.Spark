import { ShowedOn } from './showed-on';
import { TranslatedString } from './translated-string';
import { ValidationRule } from './validation-rule';

/**
 * Controls how a Reference attribute is picked in the PO-edit form.
 * Serialized as a string by the server (mirrors the .NET EReferenceDisplayType).
 */
export enum EReferenceDisplayType {
  /** Renders as a `<bs-select>` listing every referenced item. */
  Dropdown = 'Dropdown',
  /** Renders a readonly textbox + "…" button that opens a searchable modal grid picker. */
  Modal = 'Modal',
}

export interface EntityAttributeDefinition {
  id: string;
  name: string;
  label?: TranslatedString;
  /** Help text rendered as an [i] tooltip beside the label. Absent when the model declares none. */
  description?: TranslatedString;
  dataType: string;
  isRequired: boolean;
  isVisible: boolean;
  isReadOnly: boolean;
  order: number;
  query?: string;
  /** For reference attributes, specifies the target entity type's CLR type name */
  referenceType?: string;
  /** For AsDetail attributes, specifies the nested entity type's CLR type name */
  asDetailType?: string;
  /** When true, the attribute represents an array/collection of AsDetail objects */
  isArray?: boolean;
  /** For array AsDetail attributes: "modal" (default) or "inline" */
  editMode?: 'inline' | 'modal';
  /**
   * For Reference attributes: 'Modal' renders the "…" + modal query-grid picker;
   * 'Dropdown'/absent (default) renders a `<bs-select>`. Hand-set in the model JSON.
   */
  referenceDisplayType?: EReferenceDisplayType;
  /** For array AsDetail attributes: when true, rows can be drag-reordered (order = array position) */
  isSortable?: boolean;
  /**
   * When true, changing this attribute's value posts the in-progress object to
   * `/spark/po/{objectTypeId}/refresh` and applies the reshaped result as an overlay.
   * Schema-only by design — it never travels on a PersistentObjectAttribute, so a client
   * cannot claim a trigger the model did not declare.
   */
  triggersRefresh?: boolean;
  /** For LookupReference attributes, specifies the lookup reference type name */
  lookupReferenceType?: string;
  /**
   * Controls on which pages the attribute should be displayed.
   * Query = shown in list views, PersistentObject = shown in detail/edit views.
   * Can be a numeric flag value or a string like "Query, PersistentObject".
   */
  showedOn?: ShowedOn | string;
  rules: ValidationRule[];
  /** References an AttributeGroup.id to assign this attribute to a group */
  group?: string;
  /** Number of grid columns this attribute spans within a tab's column layout */
  columnSpan?: number;
  /** Renderer component name for custom display in detail/list views */
  renderer?: string;
  /** Options passed to the renderer component */
  rendererOptions?: Record<string, any>;
}

export interface AttributeTab {
  id: string;
  name: string;
  label?: TranslatedString;
  order: number;
  /** Number of columns for the grid layout within this tab */
  columnCount?: number;
}

export interface AttributeGroup {
  id: string;
  name: string;
  label?: TranslatedString;
  /** References an AttributeTab.id to assign this group to a tab */
  tab?: string;
  order: number;
}

export interface EntityType {
  id: string;
  name: string;
  description?: TranslatedString;
  /**
   * The backing CLR type, or absent for a JSON-only virtual type (#325).
   *
   * Optional on purpose: the server sends null for a virtual type, and this was declared
   * non-nullable, so any unguarded `.endsWith(...)` on it threw a TypeError naming an innocent
   * query. Types are data from the wire, not a promise about it.
   */
  clrType?: string;

  /**
   * Whether this caller may OPEN one of these, as opposed to list them.
   *
   * The catalogue is gated on Query, so its presence here means "you may list this type" and says
   * nothing about opening one. A reference must not render as a link when this is false: it used
   * to, and the click refused on arrival.
   */
  canRead?: boolean;
  alias?: string;
  /**
   * Breadcrumb template: literal text plus `{AttributeName}` placeholders. A scalar placeholder
   * renders its value; a reference placeholder renders the referenced entity's breadcrumb.
   * The server resolves this — clients only read the resulting strings. Example: "{Street}, {City}".
   */
  breadcrumb?: string;
  /**
   * When false, the breadcrumb needs the collection document (a placeholder field is not on the
   * projection). null/absent means renderable from the projection. Informational on the client.
   */
  breadcrumbProjectionSatisfiable?: boolean;
  tabs?: AttributeTab[];
  groups?: AttributeGroup[];
  attributes: EntityAttributeDefinition[];
  /** Query aliases or IDs to display as related query tables on the detail page. */
  queries?: string[];
  /**
   * The definitions of this type's AsDetail row types, sent alongside it so a detail table can draw
   * its columns.
   *
   * Needed because a row type usually has no rights of its own — a row is edited through its parent
   * — and the entity-type catalogue is gated on `Query`. Resolving a row type from the catalogue
   * therefore failed silently, and the table rendered with **no columns at all**.
   *
   * Per-caller and gated on the *parent's* right, which is the same gate that already ships each
   * row's attribute schema inside `attr.objects`. Pruned server-side: no `queryType`, `indexName`,
   * `queries` or `alias`.
   */
  detailTypes?: EntityType[];
  /**
   * Whether adding or removing a row of this type asks the server first.
   *
   * Absent or false — the default — keeps the purely client-side behaviour: New pushes a blank row,
   * Delete splices it out, and the parent's save is the first the server hears of either. On, the
   * grid calls `newObject` before showing a row and `deleteRow` before removing one, so
   * `OnNewAsync` can default it and the delete hook can refuse.
   *
   * ⚠️ Read it off the **row type**, never the parent: the type that owns the hooks owns the
   * decision. For a row type resolved out of `detailTypes` the flag survives the server-side
   * pruning, so both sources agree.
   *
   * ⚠️ It governs the round trip and nothing else. It is not a permission, and it is outside the
   * model hash, so nothing that gates a write may be conditional on it — the save path enforces the
   * row type's New/Edit/Delete rights on every embedded collection whether this is on or off.
   */
  serverSideRowLifecycle?: boolean;
}
