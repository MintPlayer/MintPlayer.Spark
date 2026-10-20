using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// What an upload token covers: every repository of its owner, or only the repositories picked on it.
/// <c>[SparkLookup]</c> generates the <c>ApiTokenScope</c> lookup with a <c>const string</c> per member
/// (<c>ApiTokenScope.Account</c>, <c>ApiTokenScope.Repository</c>); its labels are the
/// <c>translations.json</c> keys <c>lookup.EApiTokenScope.*</c>.
/// </summary>
/// <remarks>
/// String-keyed because the keys are what <see cref="CodeCoverage.Entities.ApiToken.Scope"/> has always
/// stored (<c>"Account"</c>, <c>"Repository"</c>) — an enum would persist the same names today, but
/// keying on the stored string leaves every existing token document untouched by construction. See
/// <see cref="EProjectComparison"/> for the longer version of that argument. This enum only declares
/// the lookup: no entity stores it.
/// </remarks>
[SparkLookup(StringKeys = true)]
public enum EApiTokenScope
{
    /// <summary>Every repository of the owning account. Uploads for every repository of the account.</summary>
    Account,

    /// <summary>Only the repositories listed in <see cref="CodeCoverage.Entities.ApiToken.RepositoryIds"/>. Uploads only for the selected repositories.</summary>
    Repository,
}
