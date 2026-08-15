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
}
