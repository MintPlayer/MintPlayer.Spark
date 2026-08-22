using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.AllFeatures;

public class SparkFullOptions
{
    /// <summary>
    /// Configures Spark's authentication surface — notably
    /// <see cref="SparkAuthenticationOptions.LocalCredentials"/>, which chooses how much of the
    /// email/password endpoint family to mount. When null, the full local-credential surface is
    /// mounted, as it always has been.
    /// </summary>
    /// <remarks>
    /// This gates only <em>local</em> credentials. External login providers configured through
    /// <see cref="IdentityProviders"/> are mounted in every mode.
    /// </remarks>
    public Action<SparkAuthenticationOptions>? Authentication { get; set; }

    /// <summary>
    /// Configures ASP.NET Core Identity options (password rules, lockout, etc.).
    /// When null, default identity settings are used.
    /// </summary>
    public Action<IdentityOptions>? Identity { get; set; }

    /// <summary>
    /// Configures external login providers (Google, Microsoft, OIDC, etc.).
    /// </summary>
    public Action<IdentityBuilder>? IdentityProviders { get; set; }

    /// <summary>
    /// Configures the durable message bus.
    /// When null, default messaging settings are used.
    /// </summary>
    public Action<SparkMessagingOptions>? Messaging { get; set; }

    /// <summary>
    /// Configures cross-module ETL replication.
    /// When null, replication is not enabled.
    /// </summary>
    public Action<SparkReplicationOptions>? Replication { get; set; }

    /// <summary>
    /// Anything the bundle does not model. Runs last, with the same <c>ISparkBuilder</c> every
    /// other option configures, so an app on <c>AddSparkFull</c> can still reach features this
    /// type has no property for — credential schemes, for instance.
    /// <para>
    /// Without it, <c>AddSparkFull</c> is a closed set: an app wanting
    /// <c>AddModuleCertificateAuthentication()</c> or <c>AddJwtBearerCredential(…)</c> had to
    /// abandon the bundle and hand-roll <c>AddSpark</c>, which is a lot of ceremony to add one
    /// line. A bundle that cannot be extended stops being a convenience the first time you need
    /// something it did not anticipate.
    /// </para>
    /// </summary>
    public Action<MintPlayer.Spark.Abstractions.Builder.ISparkBuilder>? Configure { get; set; }

    /// <summary>
    /// Configures the Spark rate limiter (partitioned by client IP, scoped to <c>/spark/</c>).
    /// When null, the limiter is not wired — demo/production apps opt in.
    /// Set to <c>_ =&gt; { }</c> to enable with default limits (150 requests / 10 s).
    /// </summary>
    public Action<SparkRateLimiterOptions>? RateLimiter { get; set; }
}
