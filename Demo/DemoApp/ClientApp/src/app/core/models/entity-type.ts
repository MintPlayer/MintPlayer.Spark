import { ShowedOn } from './showed-on';
import { ValidationRule } from './validation-rule';

export interface EntityAttributeDefinition {
  id: string;
  name: string;
  label?: string;
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
  /** For LookupReference attributes, specifies the lookup reference type name */
  lookupReferenceType?: string;
  /**
   * Controls on which pages the attribute should be displayed.
   * Query = shown in list views, PersistentObject = shown in detail/edit views.
   * Can be a numeric flag value or a string like "Query, PersistentObject".
   */
  showedOn?: ShowedOn | string;
  rules: ValidationRule[];
}

export interface EntityType {
  id: string;
  name: string;
  clrType: string;
  /**
   * Template string with {PropertyName} placeholders for building a formatted display value.
   * Example: "{Street}, {PostalCode} {City}"
   */
  displayFormat?: string;
  /**
   * (Fallback) Single attribute name to use as display value when displayFormat is not specified.
   */
  displayAttribute?: string;
  attributes: EntityAttributeDefinition[];
}
