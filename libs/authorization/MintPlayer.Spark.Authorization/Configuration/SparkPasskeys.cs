namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// Whether an application mounts the passkey (WebAuthn) surface.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="SparkLocalCredentials"/>, and deliberately not folded into
/// it. That option means <em>email and password</em>; a passkey is a passwordless credential, and
/// the applications that most want one are exactly the applications that set local credentials to
/// <see cref="SparkLocalCredentials.Disabled"/>. Gating passkeys behind it would force an
/// application to re-enable password sign-in in order to stop using passwords.
/// </para>
/// <para>
/// Two values rather than three. A "sign in with an existing passkey but enroll no new ones" mode is
/// a coherent thing to want when winding a credential type down, but nobody has asked for it, and
/// adding a third member later is source-compatible. Unlike the local-credential family these
/// endpoints are not a star around a single page — enrollment is reachable only by an already
/// authenticated user — so there is no dangling-link problem to design around.
/// </para>
/// </remarks>
public enum SparkPasskeys
{
    /// <summary>
    /// No passkey endpoints are mapped. They are absent from the route table rather than returning
    /// 404, matching how <see cref="SparkLocalCredentials"/> withholds its own.
    /// <para>
    /// The default, and value 0 so that an absent or unparseable configuration value cannot produce
    /// a <em>more</em> permissive posture than saying nothing. Implementing
    /// <c>IUserPasskeyStore</c> on the shared user store flips
    /// <c>UserManager.SupportsUserPasskey</c> to true for every Spark application at once; no
    /// application should silently acquire a new credential type by upgrading a package.
    /// </para>
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Passkey enrollment, management and sign-in are all mounted.
    /// </summary>
    Enabled = 1,
}
