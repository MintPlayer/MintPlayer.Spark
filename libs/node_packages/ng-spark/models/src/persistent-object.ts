import { PersistentObjectAttribute } from './persistent-object-attribute';

/**
 * Per-row action affordances (#236 G5). Present only when the entity type has a row-level rule;
 * absent otherwise, so clients fall back to the type-level permissions from
 * `GET /spark/permissions/{type}`.
 *
 * The server computes each value as the intersection of the caller's type-level right and the
 * row rule (#243) — a present block never claims more than the permissions endpoint would, so
 * letting it override the type-level answer is safe.
 */
export interface PersistentObjectPermissions {
  edit: boolean;
  delete: boolean;
}

export interface PersistentObject {
  id: string;
  name: string;
  objectTypeId: string;
  breadcrumb?: string;
  attributes: PersistentObjectAttribute[];
  /** Per-row edit/delete affordances; undefined = fall back to type-level permissions. */
  can?: PersistentObjectPermissions;
  /**
   * Custom actions withheld for THIS object, by name. Absent or empty means every action the
   * caller has the right to is offered.
   *
   * The action catalogue at `/spark/actions/{objectTypeId}` is per TYPE -- it is never told which
   * row is open -- so an action that applies to only some rows cannot be filtered there. The
   * entity actions hook decides while it has the entity in hand, and the answer arrives here.
   *
   * An affordance, not a permission: the action endpoint is still reachable and its handler still
   * refuses on its own terms. What this prevents is offering a destructive action where it cannot
   * apply.
   */
  disabledActions?: string[];
}
