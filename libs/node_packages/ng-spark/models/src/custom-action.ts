import { TranslatedString } from './translated-string';

/**
 * One action of the composed catalogue (#467, D7), as `/spark/actions/list` reports it for a type:
 * a built-in (New, Edit, Delete) or a custom action, already narrowed to what the caller may run.
 * Text arrives resolved by the server (D26).
 */
export interface CustomActionDefinition {
  name: string;
  /** `actions.{name}.label`, or the humanized name when untranslated. */
  label: TranslatedString;
  icon?: string;
  /** `actions.{name}.description`; absent when untranslated. */
  description?: TranslatedString;
  showedOn: string;
  selectionRule?: string;
  refreshOnCompleted: boolean;
  /**
   * The confirmation to ask before running, `actions.{name}.confirmation` or the explicit key the
   * catalogue names. May contain `{count}`, the number of rows about to be acted on. Absent: no prompt.
   */
  confirmation?: TranslatedString;
  /**
   * How prominently, and how warily, to present the action: 'primary', 'secondary', 'danger',
   * 'warning'. Undefined renders the neutral default.
   *
   * Presentation only — the server decides who may see and run an action. A 'danger' variant just
   * asks the button to look like what it does; pair it with a `confirmation` for anything
   * irreversible, because the colour warns and the prompt is what actually prevents the accident.
   */
  variant?: string;
  offset: number;
  /**
   * True for the framework's built-in `New`, `Edit` and `Delete` (#460 D18, #467 D7): catalogue
   * entries with a `showedOn` and a `selectionRule` like any custom action (the core's
   * `actions.json` ships them; an app overrides or removes them in its own), but run through
   * `/po/new`, the edit page and `/po/delete-many`, never `/actions/execute`. Absent for a custom action.
   */
  isDefault?: boolean;
  /**
   * The client method the action needs in the browser (`provideSparkClientMethods`), e.g.
   * `'webauthn.create'`. When it is not registered, or its `supported()` says no, the action is shown
   * disabled with the reason as its tooltip. Advisory: the server still handles a cancelled step.
   */
  requiresClient?: string;
}
