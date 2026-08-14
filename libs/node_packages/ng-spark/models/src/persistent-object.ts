import { PersistentObjectAttribute } from './persistent-object-attribute';

/**
 * Per-row action affordances (#236 G5). Present only when the entity type has a row-level rule;
 * absent otherwise, so clients fall back to the type-level permissions from
 * `GET /spark/permissions/{type}`.
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
