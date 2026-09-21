namespace CodeCoverage.Forge;

/// <summary>
/// A source-code forge that repositories can be hosted on.
/// </summary>
/// <remarks>
/// <para>
/// An enum rather than a free string because every provider-dependent decision in this codebase
/// should be an exhaustive switch the compiler checks. A new forge is a deliberate change with a
/// compile error at every site that has to answer for it — which is the opposite of a string, where
/// a forgotten site silently takes a default branch.
/// </para>
/// <para>
/// The canonical wire spelling is the <em>full lowercase name</em> (<c>github</c>, not <c>gh</c>) —
/// see <see cref="ForgeProviders.ToCanonicalString"/>. That spelling lands in document ids, in URL
/// path segments and in badge URLs, so it is effectively permanent; legibility was chosen over
/// brevity precisely because it cannot be revisited later.
/// </para>
/// </remarks>
public enum EForgeProvider
{
    /// <summary>github.com. The only provider implemented today.</summary>
    GitHub,

    /// <summary>gitlab.com. Declared so switches are exhaustive; not yet implemented.</summary>
    GitLab,

    /// <summary>Bitbucket Cloud. Declared so switches are exhaustive; not yet implemented.</summary>
    Bitbucket,
}

/// <summary>
/// The canonical spelling of an <see cref="EForgeProvider"/>, and the parse back.
/// </summary>
/// <remarks>
/// Deliberately not <c>Enum.ToString()</c>/<c>Enum.Parse</c>: those would give <c>"GitHub"</c> with
/// its capital H, and this value goes into URLs and stored ids where casing must be fixed and
/// lowercase. Keeping the mapping explicit also means renaming the enum member cannot silently
/// change a published URL.
/// </remarks>
public static class ForgeProviders
{
    /// <summary>The spelling used in document ids, route segments and badge URLs.</summary>
    public static string ToCanonicalString(this EForgeProvider provider) => provider switch
    {
        EForgeProvider.GitHub => "github",
        EForgeProvider.GitLab => "gitlab",
        EForgeProvider.Bitbucket => "bitbucket",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown forge provider."),
    };

    /// <summary>
    /// Parses a canonical spelling. Case-insensitive on the way in — a hand-typed URL should not
    /// 404 over capitalisation — while <see cref="ToCanonicalString"/> only ever emits lowercase.
    /// </summary>
    public static bool TryParse(string? value, out EForgeProvider provider)
    {
        switch (value?.ToLowerInvariant())
        {
            case "github": provider = EForgeProvider.GitHub; return true;
            case "gitlab": provider = EForgeProvider.GitLab; return true;
            case "bitbucket": provider = EForgeProvider.Bitbucket; return true;
            default: provider = default; return false;
        }
    }
}
