using MintPlayer.Spark;
using MintPlayer.Spark.IdentityProvider;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Linq;

namespace SparkId;

/// <summary>
/// SparkId has no business entities of its own: it is the identity provider HR, Fleet and QnA sign
/// in against (<c>docs/identity_provider_platform_PRD.md</c> R1). <see cref="IOidcApplicationContext"/>
/// puts the provider's clients and scopes through the PersistentObject screens.
/// </summary>
public class SparkIdContext : SparkContext, IOidcApplicationContext
{
    public IRavenQueryable<OidcApplication> OidcApplications => Session.Query<OidcApplication>();
    public IRavenQueryable<OidcScope> OidcScopes => Session.Query<OidcScope>();
}
