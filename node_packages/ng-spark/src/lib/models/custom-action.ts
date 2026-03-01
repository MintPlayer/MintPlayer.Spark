import { TranslatedString } from './translated-string';

export interface CustomActionDefinition {
  name: string;
  displayName: TranslatedString;
  icon?: string;
  description?: string;
  showedOn: string;
  selectionRule?: string;
  refreshOnCompleted: boolean;
  confirmationMessageKey?: string;
  offset: number;
}
