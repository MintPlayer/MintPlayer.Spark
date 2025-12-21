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
  rules: ValidationRule[];
}

export interface EntityType {
  id: string;
  name: string;
  clrType: string;
  displayAttribute?: string;
  attributes: EntityAttributeDefinition[];
}
