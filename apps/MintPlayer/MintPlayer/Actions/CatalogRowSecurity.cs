namespace MintPlayer.Actions;

/// <summary>
/// The catalog is public: visitors read every person, artist, song, tag and medium type, and only the
/// administrators write (security.json). Deleted rows are hidden by SoftDelete, on custom queries too.
/// </summary>
internal static class CatalogRowSecurity
{
    public const string Rationale =
        "The music catalog is public by design, as it was on the legacy site: every visitor reads every row. Writing is an administrator right in security.json, not a row rule.";
}
