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
   * Optional condition deciding whether the action is OFFERED on the object being displayed.
   * Absent means always offered.
   *
   * Presentation, never authorization. The action catalogue is fetched per TYPE -- the server is
   * not told which row is open -- so this is evaluated here, against the object already on screen,
   * and the action handler still refuses on its own terms. Hiding a button protects nothing.
   *
   * It exists because "offered to everyone, refused by the handler" is correct and still a bad
   * experience: a red, irreversible button on every row that only admits it will refuse AFTER the
   * confirmation prompt.
   */
  visibleWhen?: CustomActionVisibility;
}

/** @see CustomActionDefinition.visibleWhen */
export interface CustomActionVisibility {
  /** Attribute on the displayed object to test, by name. */
  attribute: string;
  /** Offer the action only when the attribute equals this, compared as case-insensitive text. */
  equals?: string;
  /** Offer the action only when the attribute does NOT equal this. Both may be set. */
  notEquals?: string;
}
