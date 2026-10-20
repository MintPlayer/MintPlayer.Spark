using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Organizations;

/// <summary>Options of the organizations package, bound from <c>Spark:Organizations</c>, then code.</summary>
public sealed class SparkOrganizationsOptions : ISparkConfigurationSection
{
    /// <summary>The configuration section the options are bound from.</summary>
    public static string SectionName => "Spark:Organizations";

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

    /// <summary>
    /// The custom roles a membership may carry beside its built-in role (<c>docs/commerce_PRD.md</c> X8), name →
    /// description: <c>scanner</c>, <c>finance</c>. Declared by the host (here or in configuration,
    /// <c>Spark:Organizations:CustomRoles:scanner</c>) or by a library (<c>spark.AddOrganizationRole</c>). Names
    /// match case-insensitively and follow <see cref="OrganizationCustomRoles.IsValidName"/>.
    /// </summary>
    public Dictionary<string, string> CustomRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
