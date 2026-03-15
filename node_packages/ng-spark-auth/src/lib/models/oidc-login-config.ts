export interface SparkOidcLoginConfig {
  /** Scheme name matching backend AddOidcLogin() scheme */
  scheme: string;
  /** Display name on button */
  displayName: string;
  /** Bootstrap icon name */
  icon?: string;
  /** Button color class */
  buttonClass?: string;
}
