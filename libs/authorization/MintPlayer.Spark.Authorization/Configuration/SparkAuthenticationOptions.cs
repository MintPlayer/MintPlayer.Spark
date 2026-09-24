namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// Spark-owned configuration for the authentication surface — which auth endpoints an
/// application mounts, as opposed to how ASP.NET Core Identity behaves once they are mounted
/// (that is <see cref="Microsoft.AspNetCore.Identity.IdentityOptions"/>).
/// </summary>
/// <example>
/// <code>
/// // An application that only allows GitHub sign-in — the default posture:
/// spark.AddAuthentication&lt;SparkUser&gt;(
///     configureProviders: identity =&gt; identity.AddGitHub(...));
///
/// // An application that also wants email/password sign-in:
/// spark.AddAuthentication&lt;SparkUser&gt;(
///     auth =&gt; auth.LocalCredentials = SparkLocalCredentials.Full);
/// </code>
/// </example>
public class SparkAuthenticationOptions
{
    /// <summary>
    /// How much of the local-credential surface to mount. Defaults to
    /// <see cref="SparkLocalCredentials.Disabled"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default was <see cref="SparkLocalCredentials.Full"/> and is now
    /// <see cref="SparkLocalCredentials.Disabled"/>, to match the client: <c>sparkAuthRoutes()</c>
    /// mounts nothing unless a feature asks for it, and leaving the two defaults on opposite
    /// postures is exactly the mismatch <c>SparkSignInComponent</c>'s dev-mode warning exists to
    /// catch. An application that wants password sign-in now says so.
    /// </para>
    /// <para>
    /// The password-recovery family is an account-enumeration and mail-send surface even where
    /// nobody holds a password, so the safe default is the one that mounts nothing and the
    /// application opts in.
    /// </para>
    /// <para>
    /// Leaving this at the default without registering an external provider makes an application
    /// nobody can sign into, and is rejected at startup — loudly, which is the point.
    /// </para>
    /// </remarks>
    public SparkLocalCredentials LocalCredentials { get; set; } = SparkLocalCredentials.Disabled;

    /// <summary>
    /// What to do when an external provider asserts an email that already belongs to an existing
    /// account. Defaults to <see cref="SparkExternalLoginLinking.Disabled"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disabled by default for the same reason as <see cref="LocalCredentials"/>: both linking
    /// modes are account-takeover surface if implemented carelessly, and an application with one
    /// provider cannot reach the situation at all. An application that accepts several says so.
    /// </para>
    /// <para>
    /// ⚠️ <see cref="SparkExternalLoginLinking.ConfirmByEmail"/> requires a real mail transport.
    /// The framework deliberately registers none, and the combination of that mode with the no-op
    /// sender is rejected at startup rather than silently discarding every confirmation.
    /// </para>
    /// </remarks>
    public SparkExternalLoginLinking ExternalLoginLinking { get; set; } = SparkExternalLoginLinking.Disabled;

    /// <summary>
    /// Whether the passkey (WebAuthn) surface is mounted. Defaults to
    /// <see cref="SparkPasskeys.Disabled"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Independent of <see cref="LocalCredentials"/> on purpose — see <see cref="SparkPasskeys"/>.
    /// An application with no passwords can still offer passkeys, and that is the common case.
    /// </para>
    /// <para>
    /// ⚠️ Enrollment requires an authenticated session, so enabling this does not give anyone a way
    /// <em>into</em> an account they could not already reach. A passkey is added to an account that
    /// already exists; the first credential is still an external login.
    /// </para>
    /// </remarks>
    public SparkPasskeys Passkeys { get; set; } = SparkPasskeys.Disabled;

    /// <summary>
    /// The relying-party id passkeys are bound to — normally the site's registrable domain.
    /// </summary>
    /// <remarks>
    /// ⚠️ Leave this null and the RP id is derived from the request <c>Host</c>, which is correct
    /// until a proxy gets it wrong. A passkey is bound to its RP id <em>for life</em> and there is no
    /// migration: credentials enrolled under a wrong value are unusable forever, and so are
    /// correctly-enrolled ones once the value changes. Pinning it converts a silent, permanent
    /// misconfiguration into a value that can be asserted at startup, which is worth one line of
    /// configuration.
    /// <para>
    /// Dev and production differ legitimately (<c>localhost</c> versus the real domain); a passkey
    /// enrolled against one is not meant to work against the other.
    /// </para>
    /// </remarks>
    public string? PasskeyServerDomain { get; set; }
}
