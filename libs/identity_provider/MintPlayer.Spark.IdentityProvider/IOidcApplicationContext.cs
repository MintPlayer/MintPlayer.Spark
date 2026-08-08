using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.IdentityProvider;

/// <summary>
/// Implement this on your <c>SparkContext</c> to administer OIDC clients and scopes through
/// Spark's own screens:
/// <code>
/// public class MyContext : SparkContext, IOidcApplicationContext
/// {
///     public IRavenQueryable&lt;OidcApplication&gt; OidcApplications { get; set; } = default!;
///     public IRavenQueryable&lt;OidcScope&gt; OidcScopes { get; set; } = default!;
/// }
/// </code>
/// then run the host once with <c>--spark-synchronize-model</c>.
/// <para>
/// The context property is the whole registration — the model synchronizer generates entity
/// definitions by scanning <c>IRavenQueryable&lt;T&gt;</c> properties, and does not care which
/// assembly the entity came from. The interface adds nothing at runtime; it exists so the
/// compiler tells you when a property is missing or misnamed, rather than the screens simply
/// not appearing.
/// </para>
/// <para>
/// Opting in is deliberate. These screens configure who may obtain tokens and what those tokens
/// carry, so they appear because an app asked for them, not because it referenced this package
/// — and <c>security.json</c> must restrict them accordingly. Granting <c>Everyone</c> on these
/// types hands anonymous callers the ability to register a client and mint tokens.
/// </para>
/// </summary>
public interface IOidcApplicationContext
{
    IRavenQueryable<OidcApplication> OidcApplications { get; set; }

    /// <summary>
    /// Scopes are half the configuration: an application may only be granted a scope that exists
    /// here and is enabled, so administering clients without administering scopes leaves every
    /// authorization request failing for a reason nothing on screen explains.
    /// </summary>
    IRavenQueryable<OidcScope> OidcScopes { get; set; }
}
