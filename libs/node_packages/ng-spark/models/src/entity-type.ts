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
}
