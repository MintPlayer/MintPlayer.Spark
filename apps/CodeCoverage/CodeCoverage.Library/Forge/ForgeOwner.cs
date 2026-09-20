using System.Diagnostics.CodeAnalysis;

namespace CodeCoverage.Forge;

/// <summary>
/// An account on a specific forge — the thing coverage data and management rights hang off.
/// Its stored form is <c>provider:login</c>, e.g. <c>github:mintplayer</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists at all.</b> Before it, the allowed-owner set was a bare
/// <c>string[]</c> of logins compared against <c>Repository.OwnerLogin</c>. That is safe with one
/// forge and silently wrong with two: a GitLab group named <c>microsoft</c> and a GitHub
/// organization named <c>microsoft</c> are unrelated principals that produce the <em>same string</em>,
/// and the comparison fails in the permissive direction. Carrying the provider in the value makes a
/// cross-forge match impossible rather than merely discouraged.
/// </para>
/// <para>
/// <b>Why a colon and not a slash.</b> The document-id scheme spells the provider as a path segment
/// (<c>Repositories/github/402741072</c>), and matching that here would be the obvious choice — but
/// it does not survive GitLab. GitLab namespaces nest (documented up to 20 levels) and are
/// themselves slash-delimited, so <c>gitlab/group/subgroup</c> has no unambiguous parse without an
/// out-of-band rule that "the first segment is the provider". A colon is not legal in a GitHub login
/// or a GitLab namespace path, so <see cref="IndexOf"/> on it is correct at any depth and a
/// malformed value is <em>detectable</em> rather than quietly splitting in the wrong place.
/// </para>
/// <para>
/// ⚠️ The divergence between the two delimiters is deliberate. The id form is safe with a slash only
/// because a numeric id contains none; this form is not. Do not "tidy" them into one spelling.
/// </para>
/// </remarks>
/// <param name="Provider">The forge this account lives on.</param>
/// <param name="Login">
/// The account's login, slug or namespace path <em>as the forge spells it</em> — unqualified, and
/// possibly containing slashes on GitLab.
/// </param>
public readonly record struct ForgeOwner(EForgeProvider Provider, string Login)
{
    /// <summary>Separates the provider from the login. See the type remarks for why it is not a slash.</summary>
    public const char Separator = ':';

    /// <summary>The stored and compared form, <c>provider:login</c>.</summary>
    public override string ToString() => $"{Provider.ToCanonicalString()}{Separator}{Login}";

    /// <summary>
    /// Parses the stored form. Splits on the <em>first</em> separator only, so a GitLab namespace
    /// path keeps every slash it came with.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out ForgeOwner? owner)
    {
        owner = null;
        if (string.IsNullOrEmpty(value))
            return false;

        var separator = value.IndexOf(Separator);
        if (separator <= 0 || separator == value.Length - 1)
            return false;

        if (!ForgeProviders.TryParse(value[..separator], out var provider))
            return false;

        owner = new ForgeOwner(provider, value[(separator + 1)..]);
        return true;
    }

    /// <summary>
    /// Whether two owners are the same principal. Logins are compared case-insensitively because
    /// every forge we target treats them that way, and the stored casing is whatever the forge
    /// happened to report at the time.
    /// </summary>
    public bool Matches(ForgeOwner other)
        => Provider == other.Provider
        && string.Equals(Login, other.Login, StringComparison.OrdinalIgnoreCase);
}
