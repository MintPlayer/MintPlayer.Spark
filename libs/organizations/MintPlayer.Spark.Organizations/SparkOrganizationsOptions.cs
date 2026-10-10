namespace MintPlayer.Spark.Organizations;

/// <summary>Options of the organizations package, bound from <c>Spark:Organizations</c>, then code.</summary>
public sealed class SparkOrganizationsOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Spark:Organizations";

    /// <summary>
    /// The SPA page a mailed link opens: <c>{Spark:Auth:PublicBaseUrl}{AcceptPath}/{token}?id={invitationId}</c>.
    /// Default <c>/organizations/accept</c>, the route ng-spark's <c>withOrganizationRoutes()</c> registers.
    /// </summary>
    public string AcceptPath { get; set; } = "/organizations/accept";

    /// <summary>
    /// Whether a user's personal organization is created when they sign in (D21). Default true; an
    /// application that creates it elsewhere calls <see cref="ISparkOrganizations.EnsurePersonalOrganizationAsync"/>.
    /// </summary>
    public bool CreatePersonalOrganizationOnSignIn { get; set; } = true;
}
