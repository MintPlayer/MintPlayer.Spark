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
  /**
   * How prominently, and how warily, to present the action: 'primary', 'secondary', 'danger',
   * 'warning'. Undefined renders the neutral default.
   *
   * Presentation only — the server decides who may see and run an action. A 'danger' variant just
   * asks the button to look like what it does; pair it with `confirmationMessageKey` for anything
   * irreversible, because the colour warns and the prompt is what actually prevents the accident.
   */
  variant?: string;
  offset: number;
  /**
   * True for the framework's built-in `New` and `Delete` (#460, D18): catalogue entries with a
   * `showedOn` and a `selectionRule` like any custom action (defaults: New has no rule, Delete is
   * `>0`; an app overrides them in `customActions.json`), but run through `/po/new` and
   * `/po/delete-many`, never `/actions/execute`. Absent for a custom action.
   */
  isDefault?: boolean;
}
