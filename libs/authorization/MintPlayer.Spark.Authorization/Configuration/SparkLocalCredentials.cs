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
    /// The default, and the behaviour of every Spark version before this option existed.
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
    /// </summary>
    Disabled = 2,
}
