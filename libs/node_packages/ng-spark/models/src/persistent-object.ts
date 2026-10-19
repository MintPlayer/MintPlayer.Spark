import { PersistentObjectAttribute } from './persistent-object-attribute';
import type { AssertNone, MissingMembers } from './contract-mirror';
import type * as Wire from './generated/contracts';
// Per-row action affordances (#236 G5) are generated: present only when the entity type has a row-level
// rule, each value the intersection of the caller's type-level right and the row rule (#243).
import type { PersistentObjectPermissions } from './generated/contracts';

/**
 * The client's view of `Wire.PersistentObject` (generated from the C# class, docs/guide-typescript-contracts.md).
 * Hand-written because the client treats a loaded object's `id` as a string; the check at the end of this file
 * fails the build when the server's object gains a member this mirror does not carry.
 */
export interface PersistentObject {
  id: string;
  name: string;
  objectTypeId: string;
  breadcrumb?: string;
  /**
   * Optimistic-concurrency token -- RavenDB's change vector for the document behind this object.
   * The server stamps it on every `/po/load` and expects it back on update.
   *
   * ⚠️ The check is opt-in BY PRESENCE of this field. Dropping it does not fail loudly; it makes
   * the save unconditional, so two people editing the same record both succeed and the second
   * silently overwrites the first. That is what used to happen here: `SparkClient` round-tripped
   * the token and got a 409, the browser did not and got last-write-wins, on the same endpoint.
   */
  etag?: string;
  attributes: PersistentObjectAttribute[];
  /** Per-row edit/delete affordances; undefined = fall back to type-level permissions. */
  can?: PersistentObjectPermissions;
  /**
   * Custom actions withheld for THIS object, by name. Absent or empty means every action the
   * caller has the right to is offered.
   *
   * The action catalogue at `/spark/actions/{objectTypeId}` is per TYPE -- it is never told which
   * row is open -- so an action that applies to only some rows cannot be filtered there. The
   * entity's `OnDisableActionsAsync` hook decides on the stored entity, and the answer arrives here
   * -- built-in actions (`Edit`, `Save`, `Delete`) included (#460, D13). The server asks the same
   * hook again at submit and refuses a disabled action with 403.
   */
  disabledActions?: string[];
  /**
   * Read-side facts an add-on attached to this object on load, keyed by the add-on (e.g.
   * `{ contribution: { own: true } }` on a contribution row). Server-written; never sent back.
   */
  metadata?: Record<string, any>;
  /**
   * The object this one is nested inside, sent with a refresh of an AsDetail row so the row's hook sees its
   * parent. Read-only context for the server; never set on a loaded object.
   */
  parent?: PersistentObject;
}

/** Fails to compile when the server's PersistentObject gains a member this mirror leaves out. */
export type PersistentObjectMirrorsContract = AssertNone<MissingMembers<Wire.PersistentObject, PersistentObject>>;
