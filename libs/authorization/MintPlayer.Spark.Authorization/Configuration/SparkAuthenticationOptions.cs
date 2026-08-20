namespace MintPlayer.Spark.Authorization.Configuration;

/// <summary>
/// Spark-owned configuration for the authentication surface — which auth endpoints an
/// application mounts, as opposed to how ASP.NET Core Identity behaves once they are mounted
/// (that is <see cref="Microsoft.AspNetCore.Identity.IdentityOptions"/>).
/// </summary>
/// <example>
/// <code>
/// // An application that only allows GitHub sign-in:
/// spark.AddAuthentication&lt;SparkUser&gt;(
///     auth =&gt; auth.LocalCredentials = SparkLocalCredentials.Disabled,
///     configureProviders: identity =&gt; identity.AddGitHub(...));
/// </code>
/// </example>
public class SparkAuthenticationOptions
{
    /// <summary>
    /// How much of the local-credential surface to mount. Defaults to
    /// <see cref="SparkLocalCredentials.Full"/>, so an application that does not set it keeps
    /// the endpoint set Spark has always mapped.
    /// </summary>
    /// <remarks>
    /// Setting this to <see cref="SparkLocalCredentials.Disabled"/> without registering an
    /// external provider leaves an application nobody can sign into, and is rejected at startup.
    /// </remarks>
    public SparkLocalCredentials LocalCredentials { get; set; } = SparkLocalCredentials.Full;
}
