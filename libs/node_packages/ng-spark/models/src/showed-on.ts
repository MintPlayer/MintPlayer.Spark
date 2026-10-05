/**
 * Flags enum controlling on which pages an attribute should be displayed.
 * Values can be combined: ShowedOn.Query | ShowedOn.PersistentObject
 */
export enum ShowedOn {
  /**
   * Drawn on no page (#264; Vidyano's `Never`). The attribute still ships on the object, and an action can show
   * it for one object through the runtime `showedOn` it sets on load, new or refresh.
   */
  None = 0,
  Query = 1,
  PersistentObject = 2,
}

/**
 * Helper function to check if a ShowedOn value includes a specific flag.
 */
export function hasShowedOnFlag(value: ShowedOn | string | undefined, flag: ShowedOn): boolean {
  if (value === undefined) return true; // Default: show on all pages

  // Handle string values from JSON (e.g., "Query, PersistentObject")
  if (typeof value === 'string') {
    const parts = value.split(',').map(s => s.trim());
    const flagName = ShowedOn[flag];
    return parts.includes(flagName);
  }

  // Handle numeric flag values
  return (value & flag) === flag;
}
