using System.Globalization;
using System.Security.Claims;
using System.Text;

namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>What an external provider says about the email address it asserted.</summary>
public enum SparkEmailVerification
{
    /// <summary>The provider attests the address is verified. The account is created confirmed.</summary>
    Verified,

    /// <summary>
    /// The provider has a reliable signal and it says <em>not</em> verified (or the signal could not be
    /// obtained). No account is provisioned — the callback answers <c>email_not_verified</c>.
    /// </summary>
    Unverified,

    /// <summary>
    /// The provider has no reliable verified-email signal. The account is created <b>unconfirmed</b> and a
    /// confirmation link is mailed; <see cref="SparkAuthenticationOptions.RequireConfirmedEmail"/> decides
    /// whether it can sign in before confirming.
    /// </summary>
    NoSignal,
}

/// <summary>Where a provisioned account's user name comes from.</summary>
public enum SparkUserNameSource
{
    /// <summary>
    /// A slug of the provider's display name (<see cref="ClaimTypes.Name"/>): <c>John Doe</c> →
    /// <c>john-doe</c>, then <c>john-doe-2</c>, … when taken. Never the email's local part. Editable on
    /// the profile page. The default (#460, D7).
    /// </summary>
    DisplayNameSlug,

    /// <summary>
    /// The provider's own unique handle, verbatim (<see cref="ClaimTypes.Name"/>, falling back to
    /// <see cref="ClaimTypes.NameIdentifier"/>). For providers whose name claim <em>is</em> a unique
    /// handle that the application relies on — GitHub's login.
    /// </summary>
    ProviderHandle,
}

/// <summary>
/// How the external-login callback treats one provider's first sign-in (#460, D7): whether its email
/// can be trusted as verified, and how the new account's user name is chosen.
/// </summary>
public sealed class SparkExternalProviderPolicy
{
    /// <summary>The standard OIDC claim the default policy and most presets read.</summary>
    public const string EmailVerifiedClaim = "email_verified";

    /// <summary>Reads the provider's verified-email signal from the external principal.</summary>
    public required Func<ClaimsPrincipal, SparkEmailVerification> EmailVerification { get; init; }

    /// <summary>Where the user name comes from. Defaults to <see cref="SparkUserNameSource.DisplayNameSlug"/>.</summary>
    public SparkUserNameSource UserName { get; init; } = SparkUserNameSource.DisplayNameSlug;

    /// <summary>
    /// The policy for a scheme with no registered entry: <c>email_verified=true</c> → verified,
    /// anything else → <see cref="SparkEmailVerification.Unverified"/> (fail closed — a provider nobody
    /// described is not assumed to lack a signal), user name from the display-name slug.
    /// </summary>
    public static SparkExternalProviderPolicy Default { get; } = new()
    {
        EmailVerification = principal => HasTrueClaim(principal, EmailVerifiedClaim)
            ? SparkEmailVerification.Verified
            : SparkEmailVerification.Unverified,
    };

    /// <summary>A policy for a provider that never attests an email: every sign-up is <see cref="SparkEmailVerification.NoSignal"/>.</summary>
    public static SparkExternalProviderPolicy WithoutVerifiedEmailSignal(SparkUserNameSource userName = SparkUserNameSource.DisplayNameSlug) => new()
    {
        EmailVerification = _ => SparkEmailVerification.NoSignal,
        UserName = userName,
    };

    /// <summary>Whether <paramref name="principal"/> carries <paramref name="claimType"/> = <c>true</c> (case-insensitive).</summary>
    public static bool HasTrueClaim(ClaimsPrincipal principal, string claimType)
        => string.Equals(principal.FindFirstValue(claimType), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A user-name slug: lower-case ASCII letters and digits separated by single hyphens, diacritics
    /// folded, at most 32 characters; <c>user</c> when nothing usable remains.
    /// </summary>
    public static string Slugify(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return "user";

        var decomposed = displayName.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingHyphen = false;

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            var lower = char.ToLowerInvariant(c);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingHyphen && builder.Length > 0)
                    builder.Append('-');
                pendingHyphen = false;
                builder.Append(lower);
            }
            else
            {
                pendingHyphen = true;
            }

            if (builder.Length >= 32)
                break;
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "user" : slug;
    }
}
