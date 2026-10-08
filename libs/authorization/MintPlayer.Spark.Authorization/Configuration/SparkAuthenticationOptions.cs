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
    /// What password sign-in accepts as the identifier: the email, the user name, or either.
    /// Defaults to <c><see cref="SparkSignInIdentifiers.Email"/> | <see cref="SparkSignInIdentifiers.UserName"/></c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A disallowed kind is refused exactly like an unknown account — the same 401, and no lookup —
    /// so the setting is invisible to someone probing for accounts. It is not a secret either:
    /// <c>/spark/auth/capabilities</c> reports it, and ng-spark/auth's login page labels its field from
    /// it.
    /// </para>
    /// <para>
    /// ⚠️ A value allowing neither is rejected at startup whenever password sign-in is mounted: it
    /// would be an application nobody can sign into with a password. Under
    /// <see cref="SparkLocalCredentials.Disabled"/> there is no password sign-in and the value is not
    /// read.
    /// </para>
    /// </remarks>
    public SparkSignInIdentifiers SignInIdentifiers { get; set; } = SparkSignInIdentifiers.Email | SparkSignInIdentifiers.UserName;

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
    /// Whether a signed-in user may change their email address. Defaults to
    /// <see cref="SparkEmailChange.Disabled"/> — see <see cref="SparkEmailChange"/>.
    /// </summary>
    public SparkEmailChange EmailChange { get; set; } = SparkEmailChange.Disabled;

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

    /// <summary>
    /// Refuse sign-in to accounts whose email is not confirmed (#460, D6). Defaults to
    /// <see langword="false"/>. Sets <c>IdentityOptions.SignIn.RequireConfirmedEmail</c>, and the
    /// external-login callback honours it for accounts it provisions unconfirmed.
    /// </summary>
    /// <remarks>
    /// Completing a password reset confirms the email (the reset link was delivered to it), and
    /// <c>forgotPassword</c> sends to unconfirmed addresses, so an account created before confirmation
    /// was required can always get in.
    /// </remarks>
    public bool RequireConfirmedEmail { get; set; }

    /// <summary>
    /// Allows a registration surface (<see cref="SparkLocalCredentials.Full"/>) with no mail transport.
    /// Default <see langword="false"/>: startup refuses registration while every account mail would be
    /// discarded by Identity's no-op sender — confirmation and reset links would never arrive (#460,
    /// D6). Also read from <c>Spark:Auth:AllowUnconfirmedRegistration</c>.
    /// </summary>
    public bool AllowUnconfirmedRegistration { get; set; }

    /// <summary>
    /// How recent a sign-in must be for operations that require re-authentication when no password is
    /// supplied (account deletion). Defaults to 5 minutes.
    /// </summary>
    public TimeSpan ReauthenticationMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Where mailed links point (#460, D16). See <see cref="SparkAuthLinkOptions"/>.
    /// </summary>
    public SparkAuthLinkOptions Links { get; set; } = new();

    /// <summary>
    /// The issuer shown by authenticator apps for this application's TOTP entry. Defaults to the host's
    /// application name.
    /// </summary>
    public string? AuthenticatorIssuer { get; set; }

    /// <summary>
    /// Per-provider sign-up policy — verified-email trust and user-name source (#460, D7), keyed by
    /// authentication scheme (case-insensitive). The Spark provider presets (<c>AddGitHub</c>,
    /// <c>AddSparkGoogle</c>, …) fill their own entry; a scheme without one gets
    /// <see cref="SparkExternalProviderPolicy.Default"/>.
    /// </summary>
    public IDictionary<string, SparkExternalProviderPolicy> ExternalProviders { get; } =
        new Dictionary<string, SparkExternalProviderPolicy>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Run <see cref="Identity.SparkUserBackfill{TUser}"/> once after start. Defaults to
    /// <see langword="true"/>; the pass is a no-op after its completion marker exists.
    /// </summary>
    public bool BackfillUsersOnStartup { get; set; } = true;
}

/// <summary>
/// Where confirmation and reset links in mail point: the SPA's pages, whose components post the token
/// back to the API (#460, D16).
/// </summary>
/// <remarks>
/// ⚠️ <see cref="PublicBaseUrl"/> must be set outside Development. Deriving a link from the request's
/// <c>Host</c> header on an anonymous endpoint (<c>forgotPassword</c>) is the password-reset-poisoning
/// hole — an attacker sends the request with their own host and the victim's reset token arrives in a
/// link to it. Unset outside Development, a link-bearing mail is not sent (logged as an error); in
/// Development the request origin is used.
/// </remarks>
public class SparkAuthLinkOptions
{
    /// <summary>The SPA's public origin, e.g. <c>https://example.com</c>. Bound from <c>Spark:Auth:PublicBaseUrl</c> when unset in code.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>The page that confirms an email (and a changed email). Default <c>/confirm-email</c>; receives <c>userId</c>, <c>code</c> and, for a change, <c>changedEmail</c>.</summary>
    public string ConfirmEmailPath { get; set; } = "/confirm-email";

    /// <summary>The page that completes a password reset. Default <c>/reset-password</c> (ng-spark/auth's); receives <c>email</c> and <c>code</c>.</summary>
    public string ResetPasswordPath { get; set; } = "/reset-password";
}
