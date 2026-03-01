import { TranslatedString } from './translated-string';

export interface ValidationError {
  attributeName: string;
  errorMessage: TranslatedString;
  ruleType: string;
}

export interface ValidationErrorResponse {
  errors: ValidationError[];
}
