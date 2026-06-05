import { PersistentObject } from './persistent-object';
import { ShowedOn } from './showed-on';
import { TranslatedString } from './translated-string';
import { ValidationRule } from './validation-rule';

export interface PersistentObjectAttribute {
  id: string;
  name: string;
  label?: TranslatedString;
  value?: any;
  dataType: string;
  isArray?: boolean;
  isRequired: boolean;
  isVisible: boolean;
  isReadOnly: boolean;
  order: number;
  query?: string;
  breadcrumb?: string;
  /**
   * Controls on which pages the attribute should be displayed.
   * Query = shown in list views, PersistentObject = shown in detail/edit views.
   * Can be a numeric flag value or a string like "Query, PersistentObject".
   */
  showedOn?: ShowedOn | string;
  isValueChanged?: boolean;
  rules: ValidationRule[];
  group?: string;
  /** Renderer component name for custom display in detail/list views */
  renderer?: string;
  /** Options passed to the renderer component */
  rendererOptions?: Record<string, any>;

  /**
   * When `dataType === 'AsDetail'` and `isArray === false`: the single nested
   * PersistentObject carrying the detail entity's values. `null` when the field is unset.
   * Mirrors the server's PersistentObjectAttributeAsDetail.Object.
   */
  object?: PersistentObject | null;
  /**
   * When `dataType === 'AsDetail'` and `isArray === true`: the nested PO collection, one
   * per array element. Mirrors PersistentObjectAttributeAsDetail.Objects.
   */
  objects?: PersistentObject[] | null;
  /**
   * CLR type name of the detail entity for AsDetail attributes (e.g. `HR.Entities.Address`).
   * Emitted by the server so the frontend can look up the matching EntityType metadata
   * without re-resolving through `asDetailType` on the schema definition.
   */
  asDetailType?: string;
}
