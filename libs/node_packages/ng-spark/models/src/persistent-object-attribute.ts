import { PersistentObject } from './persistent-object';
import { ShowedOn } from './showed-on';
import { TranslatedString } from './translated-string';
import type { AssertNone, MissingMembers } from './contract-mirror';
import type * as Wire from './generated/contracts';
import type { PersistentObjectAttributeOption, ValidationRule } from './generated/contracts';

/**
 * The client's view of `Wire.PersistentObjectAttribute` and its AsDetail subclass (generated from the C# classes,
 * docs/guide-typescript-contracts.md), merged into one interface as the client reads them. Hand-written because
 * the client builds attributes itself and relies on narrower members than the wire states; the check at the end
 * of this file fails the build when the server's attribute gains a member this mirror does not carry.
 */
export interface PersistentObjectAttribute {
  id: string;
  name: string;
  label?: TranslatedString;
  value?: any;
  dataType: string;
  isArray?: boolean;
  isRequired: boolean;
  isReadOnly: boolean;
  order: number;
  query?: string;
  breadcrumb?: string;
  /**
   * For `dataType === 'Reference'` with `isArray === true`: resolved display label per
   * referenced id (id → breadcrumb), so the UI can render a chip per selected reference.
   * The single-reference counterpart is `breadcrumb`.
   */
  breadcrumbs?: Record<string, string | null>;
  /**
   * Controls on which pages the attribute should be displayed.
   * Query = shown in list views, PersistentObject = shown in detail/edit views.
   * Can be a numeric flag value or a string like "Query, PersistentObject".
   */
  showedOn?: ShowedOn | string;
  isValueChanged?: boolean;
  rules: ValidationRule[];
  group?: string;
  /** Renderer component name for custom display in detail/list views */
  renderer?: string;
  /** Options passed to the renderer component */
  rendererOptions?: Record<string, any>;
  /**
   * The selectable values, when a refresh hook replaced them. `null` or absent (the normal case) means
   * unchanged: keep what the attribute's own source gave (its query, or `/spark/lookupref`). An empty array
   * means "no options", which is distinct from null.
   */
  options?: PersistentObjectAttributeOption[] | null;

  /**
   * When `dataType === 'AsDetail'` and `isArray === false`: the single nested
   * PersistentObject carrying the detail entity's values. `null` when the field is unset.
   * Mirrors the server's PersistentObjectAttributeAsDetail.Object.
   */
  object?: PersistentObject | null;
  /**
   * When `dataType === 'AsDetail'` and `isArray === true`: the nested PO collection, one
   * per array element. Mirrors PersistentObjectAttributeAsDetail.Objects.
   */
  objects?: PersistentObject[] | null;
  /**
   * CLR type name of the detail entity for AsDetail attributes (e.g. `HR.Entities.Address`).
   * Emitted by the server so the frontend can look up the matching EntityType metadata
   * without re-resolving through `asDetailType` on the schema definition.
   */
  asDetailType?: string;
}

/** Fails to compile when the server's attribute (either class) gains a member this mirror leaves out. */
export type PersistentObjectAttributeMirrorsContract = AssertNone<MissingMembers<Wire.PersistentObjectAttributeAsDetail, PersistentObjectAttribute>>;
