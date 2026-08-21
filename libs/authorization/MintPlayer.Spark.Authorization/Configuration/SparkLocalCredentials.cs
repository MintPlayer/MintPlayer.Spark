namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// How much of the local-credential (email + password) surface an application mounts.
/// </summary>
/// <remarks>
/// This is deliberately a three-value mode rather than a set of per-endpoint switches. The
/// local-credential endpoints are not independent of one another: the password-recovery family
/// (<c>forgotPassword</c>, <c>resetPassword</c>, <c>confirmEmail</c>, <c>resendConfirmationEmail</c>)
/// is an account-enumeration and mail-send surface even in an application where nobody can hold a
/// password, so closing <c>register</c> on its own buys very little. On the client the same is true
/// for a different reason — the pages form a star centred on the login page, so removing any proper
/// subset leaves a dangling link. The three values below are the configurations that are actually
/// coherent on both tiers.
/// </remarks>
public enum SparkLocalCredentials
{
    /// <summary>
    /// Everything: registration, password sign-in, and the full password-recovery family.
    /// <para>
    /// No longer the default. It stays value 0 so that a configuration binder reading an absent or
    /// unparseable value does not silently produce a <em>different</em> posture than an explicit
    /// one — the default is chosen in <see cref="SparkAuthenticationOptions.LocalCredentials"/>,
    /// where it is visible, rather than by which enum member happens to be first.
    /// </para>
    /// </summary>
    Full = 0,

    /// <summary>
    /// Password sign-in and recovery, but no self-service registration. For applications whose
    /// accounts are provisioned by an administrator and still need password reset.
    /// </summary>
    SignInOnly = 1,

    /// <summary>
    /// No local credentials at all — sign-in happens exclusively through an external provider.
    /// The endpoints are not mapped, so they are absent from the route table rather than merely
    /// returning 404.
    /// <para>
    /// The default since preview.60, matching the client's opt-in routes.
    /// </para>
    /// </summary>
    Disabled = 2,
}
