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
}
