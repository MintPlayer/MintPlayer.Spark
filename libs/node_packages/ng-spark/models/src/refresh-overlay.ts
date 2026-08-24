import { EntityAttributeDefinition } from './entity-type';
import { PersistentObject } from './persistent-object';
import { ValidationRule } from './validation-rule';

/**
 * One selectable value, as replaced by a refresh hook. Mirrors the server's
 * `PersistentObjectAttributeOption`.
 */
export interface RefreshedOption {
  key: string;
  label?: Record<string, string>;
}

/**
 * What a refresh changed about one attribute's *presentation*.
 *
 * Kept separate from `EntityType` on purpose. The form's option loading hangs off a single effect
 * keyed on `entityType` identity, and `SparkService` caches nothing — so applying a refresh by
 * setting a new `EntityType` re-issues every reference query, every lookup fetch, a full
 * `getEntityTypes()` and a `getPermissions()` per array-AsDetail attribute, on every refresh.
 * Mutating the existing object instead is inert, because the rendering computed would not re-run.
 * An overlay is the only shape that is both reactive and free.
 */
export interface AttributeOverlay {
  isRequired?: boolean;
  isReadOnly?: boolean;
  isVisible?: boolean;
  rules?: ValidationRule[];
  query?: string;
  /** `undefined` means the hook did not touch the options; an empty array means there are none. */
  options?: RefreshedOption[];
}

export type RefreshOverlay = Record<string, AttributeOverlay>;

/** Applies an overlay to one attribute definition, returning a new object when anything changed. */
export function applyOverlay(
  attr: EntityAttributeDefinition,
  overlay: AttributeOverlay | undefined,
): EntityAttributeDefinition {
  if (!overlay) return attr;

  return {
    ...attr,
    isRequired: overlay.isRequired ?? attr.isRequired,
    isReadOnly: overlay.isReadOnly ?? attr.isReadOnly,
    isVisible: overlay.isVisible ?? attr.isVisible,
    rules: overlay.rules ?? attr.rules,
    query: overlay.query ?? attr.query,
  };
}

/**
 * Reads a refresh response into an overlay.
 *
 * Everything here is presentation the server owns outright, so it is taken verbatim — there is no
 * merging to do on this half, only on values.
 */
export function overlayFromResponse(response: PersistentObject): RefreshOverlay {
  const overlay: RefreshOverlay = {};

  for (const attr of response.attributes ?? []) {
    overlay[attr.name] = {
      isRequired: attr.isRequired,
      isReadOnly: attr.isReadOnly,
      isVisible: attr.isVisible,
      rules: attr.rules ?? [],
      query: attr.query,
      options: (attr as { options?: RefreshedOption[] | null }).options ?? undefined,
    };
  }

  return overlay;
}

/**
 * Merges a refresh response's values into the live form.
 *
 * The rule, and the reason for it: a refresh is not instant, and the user keeps typing during it —
 * the form is deliberately never frozen. So for each attribute we ask whether the *server* changed
 * it, by comparing the response against the values that were **sent**, not against what is on
 * screen now.
 *
 * - server value equals what we sent → the hook did not touch it, so whatever is in the form now
 *   wins, including anything typed while the request was in flight;
 * - server value differs → the hook deliberately changed it, and it wins over a concurrent edit.
 *
 * Comparing against the displayed value instead is the classic "refresh eats my typing" bug;
 * refusing to overwrite anything the user touched is the equally wrong opposite, where a dependent
 * field the hook computed never appears.
 *
 * @param sent      values as they were POSTed, captured before the request left
 * @param current   values as they are now, which may have moved on
 * @param response  the reshaped object
 */
export function mergeRefreshValues(
  sent: Record<string, any>,
  current: Record<string, any>,
  response: PersistentObject,
): Record<string, any> {
  const merged = { ...current };

  for (const attr of response.attributes ?? []) {
    const serverValue = attr.value ?? null;
    const sentValue = sent[attr.name] ?? null;

    if (!valuesEqual(serverValue, sentValue)) {
      merged[attr.name] = attr.value;
    }
  }

  return merged;
}

/**
 * Structural for arrays, `===` otherwise.
 *
 * Multi-reference attributes hold `string[]`, and a fresh array of the same ids is a different
 * object — so reference equality would report every one of them as "changed by the server" on
 * every refresh, and clobber in-flight edits to them.
 */
function valuesEqual(a: any, b: any): boolean {
  if (a === b) return true;
  if (Array.isArray(a) && Array.isArray(b)) {
    return a.length === b.length && a.every((item, i) => valuesEqual(item, b[i]));
  }
  // Values arrive off the wire as JSON scalars; anything else compares by JSON shape rather than
  // by identity, which is what a caller means by "did this change".
  if (a !== null && b !== null && typeof a === 'object' && typeof b === 'object') {
    return JSON.stringify(a) === JSON.stringify(b);
  }
  return false;
}
