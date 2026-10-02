using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// What an upload token covers: every repository of its owner, or only the repositories picked on it.
/// </summary>
/// <remarks>
/// String-keyed because the keys are what <see cref="CodeCoverage.Entities.ApiToken.Scope"/> has always
/// stored (<c>"Account"</c>, <c>"Repository"</c>) — an enum would persist the same names today, but
/// keying on the stored string leaves every existing token document untouched by construction. See
/// <see cref="ProjectComparison"/> for the longer version of that argument.
/// </remarks>
public sealed class ApiTokenScope : TransientLookupReference<string>
{
    private ApiTokenScope() { }

    /// <summary>Every repository of the owning account.</summary>
    public const string Account = "Account";

    /// <summary>Only the repositories listed in <see cref="CodeCoverage.Entities.ApiToken.RepositoryIds"/>.</summary>
    public const string Repository = "Repository";

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<ApiTokenScope> Items { get; } =
    [
        new ApiTokenScope
        {
            Key = Account,
            Description = "Uploads for every repository of the account",
            Values = _TS("All repositories of the account", "Tous les dépôts du compte", "Alle repositories van het account"),
        },
        new ApiTokenScope
        {
            Key = Repository,
            Description = "Uploads only for the selected repositories",
            Values = _TS("Selected repositories", "Dépôts sélectionnés", "Geselecteerde repositories"),
        },
    ];
}
