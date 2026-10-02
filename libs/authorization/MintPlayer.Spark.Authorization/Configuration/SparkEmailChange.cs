namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// Whether a signed-in user may change their account's email address.
/// </summary>
/// <remarks>
/// Most applications never want this: the address is often the one an external provider attested, or
/// the one an administrator provisioned, and moving it is a support decision rather than self-service.
/// So it is opt-in, and an application that wants it says so.
/// </remarks>
public enum SparkEmailChange
{
    /// <summary>
    /// A new address on <c>POST /spark/auth/manage/info</c> is refused, no change mail is sent, and a
    /// change link already in a mailbox no longer confirms. Password changes through the same endpoint
    /// are unaffected.
    /// <para>
    /// The default, and value 0 so that an absent or unparseable configuration value cannot produce a
    /// <em>more</em> permissive posture than saying nothing.
    /// </para>
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// The user may request a new address; it takes effect once the link mailed to the new address is
    /// followed. Requires a <see cref="SparkLocalCredentials"/> other than
    /// <see cref="SparkLocalCredentials.Disabled"/>, which is what maps <c>POST manage/info</c>.
    /// </summary>
    Enabled = 1,
}
