using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// Whether a repository deletes a merged pull request's head branch, or defers to its account.
/// </summary>
/// <remarks>
/// Three states, named rather than implied. The obvious modelling — a <c>bool?</c> where null means
/// "inherit" — does not survive this stack: a lookup does not change an attribute's
/// <c>dataType</c>, so a nullable bool renders as a two-state checkbox; the edit form coerces null
/// to false before rendering; and the empty-string key a null lookup row produces is swallowed by
/// <c>EntityMapper</c>'s conversion <c>catch</c>, so saving "inherit" would silently do nothing.
/// <para>
/// An enum sidesteps all of that — Spark maps any enum to <c>"string"</c>, which takes the lookup
/// branch — and it is the better model anyway: each state has a name and a translated label the
/// user reads, and a migration can name <see cref="Inherit"/> where it could not name an absence.
/// </para>
/// <para>
/// <c>[SparkLookup]</c> generates the <c>DeleteBranchPolicy</c> lookup, rendered as a dropdown; its labels are the
/// <c>translations.json</c> keys <c>lookup.EDeleteBranchPolicy.*</c>. No registration:
/// <c>LookupReferenceDiscoveryService</c> scans loaded assemblies by base type.
/// </para>
/// </remarks>
[SparkLookup]
public enum EDeleteBranchPolicy
{
    /// <summary>Defer to the owning account's setting. The default for every repository. Lookup: use the account's setting.</summary>
    Inherit,

    /// <summary>Always delete the head branch of a merged pull request in this repository.</summary>
    Enabled,

    /// <summary>Never delete, whatever the account says.</summary>
    Disabled,
}
