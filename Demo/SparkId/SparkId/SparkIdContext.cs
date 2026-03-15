using MintPlayer.Spark;
using Raven.Client.Documents.Linq;
using SparkId.Entities;

namespace SparkId;

public class SparkIdContext : SparkContext
{
    public IRavenQueryable<OidcApplication> OidcApplications => Session.Query<OidcApplication>();
    public IRavenQueryable<OidcScope> OidcScopes => Session.Query<OidcScope>();
}
