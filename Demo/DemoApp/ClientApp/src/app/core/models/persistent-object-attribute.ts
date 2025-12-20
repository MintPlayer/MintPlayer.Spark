import { ValidationRule } from './validation-rule';

export interface PersistentObjectAttribute {
  id: string;
  name: string;
  label?: string;
  value?: any;
  dataType: string;
  isRequired: boolean;
  isVisible: boolean;
  isReadOnly: boolean;
  order: number;
  query?: string;
  breadcrumb?: string;
  rules: ValidationRule[];
}
