import { ShowedOn } from './showed-on';
import { TranslatedString } from './translated-string';
import { ValidationRule } from './validation-rule';

export interface EntityAttributeDefinition {
  id: string;
  name: string;
  label?: TranslatedString;
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
  clrType: string;
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
