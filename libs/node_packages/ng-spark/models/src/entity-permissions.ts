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
}
