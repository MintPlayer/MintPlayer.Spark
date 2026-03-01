import { TranslatedString } from './translated-string';

export interface ValidationRule {
  type: string;
  value?: any;
  min?: number;
  max?: number;
  message?: TranslatedString;
}
