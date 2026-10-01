export interface EntityPermissions {
  /**
   * Whether the caller may list this type. Independently grantable from `canRead` — `Query/Person`
   * alone lists rows while refusing a by-id load — and reported since preview.60. The combined
   * `QueryRead` right bundles the two invisibly, which is why this was the one action introspection
   * never mentioned.
   */
  canQuery: boolean;
  canRead: boolean;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
  /**
   * SoftDelete (#460): `Restore/T`, `Purge/T` and `ViewDeleted/T` (the recycle bin — query and load
   * with `deleted: 'include' | 'only'`). Type-level only; a row may still refuse. Optional because a
   * server older than 11.0.0-preview.91 does not send them — treat absent as `false`.
   */
  canRestore?: boolean;
  canPurge?: boolean;
  canViewDeleted?: boolean;
  /** History (#460): `History/T` (list and read revisions) and `Revert/T` together with `Edit/T`. */
  canViewHistory?: boolean;
  canRevert?: boolean;
  /** Contributions: `RevertContribution/T` (make a contribution current again; a generated contribution type only). */
  canRevertContribution?: boolean;
}
