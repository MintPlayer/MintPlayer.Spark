using System.Reflection;
using CodeCoverage.Entities;
using MintPlayer.Spark.Abstractions.Authorization;

[assembly: SparkReservedActions(typeof(CodeCoverage.Services.OwnerSettingsRights))]

namespace CodeCoverage.Services;

/// <summary>
/// The <c>security.json</c> verb this application asks for when it decides who reads an owner's settings (D-A6):
/// <c>ReadSettings/Account</c> and <c>ReadSettings/Repository</c>, granted to the Administrators. Declared so the SPARK011
/// analyzer and the startup check know Spark asks for it, and so no custom action can be named like it.
/// </summary>
public static class OwnerSettingsRights
{
    /// <summary>Read every account's (or repository's) owner-only settings, without managing it.</summary>
    public const string ReadSettings = "ReadSettings";
}

/// <summary>
/// Who may read an owner's settings, and which attributes of an <see cref="Account"/> and a <see cref="Repository"/> are
/// public (dependency-updates PRD §13.12, D-A1–D-A8). Shared by <c>AccountActions</c> and <c>RepositoryActions</c>, whose
/// <c>GetProtectedAttributesAsync</c> hide everything else from anyone who may not read the owner's settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deny by default.</b> The lists name what is public; every other property is protected, so a setting added later is
/// owner-only without anybody remembering to say so. <c>OwnerPrivacyEndToEndTests</c> holds its own copy of both lists and
/// fails on a model attribute that is on neither, so making something public is a decision a test records.
/// </para>
/// <para>
/// The redaction runs on every read path Spark has (the detail load, query rows, breadcrumbs, refresh, retry, the write
/// shield). What it cannot close is a sort or filter oracle on a query (Spark decides those per query, not per row), which
/// is why the account grid is the Administrators' alone (<c>security.json</c>, D-A2) and why the repository grids show only
/// public attributes.
/// </para>
/// </remarks>
public static class OwnerSettings
{
    /// <summary>An account's public face: the forge's own public profile, and whether we are connected to it.</summary>
    public static readonly IReadOnlySet<string> PublicAccountAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Account.Login), nameof(Account.Type), nameof(Account.AvatarUrl), nameof(Account.Provider),
        nameof(Account.Connection), nameof(Account.DisconnectedReason), nameof(Account.DisconnectedAtUtc),
    };

    /// <summary>A repository's public face, for a caller who may see the repository at all: identity, coverage, connection.</summary>
    public static readonly IReadOnlySet<string> PublicRepositoryAttributes = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Repository.Account), nameof(Repository.Archived), nameof(Repository.Connection), nameof(Repository.DefaultBranch),
        nameof(Repository.DisconnectedAtUtc), nameof(Repository.DisconnectedReason), nameof(Repository.FullName),
        nameof(Repository.IsPrivate), nameof(Repository.LatestCoverage), nameof(Repository.LatestCoverageAtUtc),
        nameof(Repository.LatestCoverageSha), nameof(Repository.Name), nameof(Repository.OwnerLogin), nameof(Repository.Provider),
    };

    /// <summary>Every property of <see cref="Account"/> that is not public.</summary>
    public static readonly IReadOnlyCollection<string> ProtectedAccountAttributes = NotPublic(typeof(Account), PublicAccountAttributes);

    /// <summary>Every property of <see cref="Repository"/> that is not public.</summary>
    public static readonly IReadOnlyCollection<string> ProtectedRepositoryAttributes = NotPublic(typeof(Repository), PublicRepositoryAttributes);

    /// <summary>
    /// Whether the caller may read the settings of <paramref name="ownerKey"/>: a manager of that owner, or anyone
    /// holding <c>ReadSettings/{type}</c> in <c>security.json</c> (the Administrators).
    /// </summary>
    /// <remarks>Both answers are memoized per request, so asking per row costs nothing after the first.</remarks>
    public static async Task<bool> CanReadAsync(ISparkVisibility visibility, IPermissionService permissions, string ownerKey, string type)
        => await visibility.CanManageOwnerAsync(ownerKey)
           || await permissions.IsAllowedAsync(OwnerSettingsRights.ReadSettings, type);

    private static string[] NotPublic(Type type, IReadOnlySet<string> publicNames)
        => [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.Name != "Id" && !publicNames.Contains(p.Name))
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)];
}
