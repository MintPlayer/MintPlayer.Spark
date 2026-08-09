using HR.Entities;
using HR.Indexes;
using HR.Replicated;
using MintPlayer.Spark;
using MintPlayer.Spark.IdentityProvider;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents.Linq;

namespace HR;

/// <summary>
/// <see cref="IOidcApplicationContext"/> is the entire OIDC admin registration: the two properties
/// below put the identity provider's own entities through the model synchronizer, and HR gets
/// screens for them like any other type. Nothing else in this app knows they came from a package.
/// <para>
/// See <c>App_Data/security.json</c> for the other half — these screens decide who may obtain
/// tokens, so they are granted to Administrators alone. HR is the demo host for this because it
/// runs deny-by-default authorization; wiring them into an app with
/// <c>AllowAnonymousAccess()</c> would publish a client-registration endpoint to the internet.
/// </para>
/// </summary>
public class HRContext : SparkContext, IOidcApplicationContext
{
    public IRavenQueryable<Person> People => Session.Query<Person>();
    public IRavenQueryable<VPerson> VPeople => Session.Query<VPerson, People_Overview>();
    public IRavenQueryable<Company> Companies => Session.Query<Company>();
    public IRavenQueryable<Profession> Professions => Session.Query<Profession>();
    public IRavenQueryable<Car> Cars => Session.Query<Car>();

    public IRavenQueryable<OidcApplication> OidcApplications => Session.Query<OidcApplication>();
    public IRavenQueryable<OidcScope> OidcScopes => Session.Query<OidcScope>();
}
