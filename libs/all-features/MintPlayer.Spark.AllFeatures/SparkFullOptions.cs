using Microsoft.AspNetCore.Identity;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.AllFeatures;

public class SparkFullOptions
{
    /// <summary>
    /// Configures Spark group-based authorization.
    /// When null, default authorization settings are used.
    /// </summary>
    public Action<AuthorizationOptions>? Authorization { get; set; }

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
    /// Configures the Spark rate limiter (partitioned by client IP, scoped to <c>/spark/</c>).
    /// When null, the limiter is not wired — demo/production apps opt in.
    /// Set to <c>_ =&gt; { }</c> to enable with default limits (150 requests / 10 s).
    /// </summary>
    public Action<SparkRateLimiterOptions>? RateLimiter { get; set; }
}
