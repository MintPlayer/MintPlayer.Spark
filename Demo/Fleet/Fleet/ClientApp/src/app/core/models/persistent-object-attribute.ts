import { ShowedOn } from './showed-on';
import { TranslatedString } from './translated-string';
import { ValidationRule } from './validation-rule';

export interface PersistentObjectAttribute {
  id: string;
  name: string;
  label?: TranslatedString;
  value?: any;
  dataType: string;
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
}
