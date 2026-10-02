using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Indexes;

/// <summary>
/// The queryable shape of an upload token: the token's own fields, plus the owner's login, key and
/// forge read from its <see cref="ApiToken.Account"/> through <see cref="ApiTokens_Overview"/>.
/// </summary>
/// <remarks>
/// The three account fields live here and not on <see cref="ApiToken"/>, because on the token they
/// were copies — and a copy of an owner's login is wrong the moment the account is renamed. The
/// index re-reads them from the account whenever either document changes.
/// <para>
/// <see cref="ApiToken.Hash"/> is deliberately absent: Spark projects only declared attributes, and
/// the hash must never reach the wire.
/// </para>
/// </remarks>
[FromIndex(typeof(ApiTokens_Overview))]
public partial class VApiToken
{
    public string? Id { get; set; }

    [LookupReference(typeof(ApiTokenScope))]
    public string Scope { get; set; } = ApiTokenScope.Account;

    [Reference(typeof(Account))]
    public string? Account { get; set; }

    /// <summary>The owner's current login, from the account. Display only.</summary>
    public string? AccountLogin { get; set; }

    /// <summary>The owner as <c>provider:login</c>, from the account — what the row filter's owners are.</summary>
    public string? AccountOwnerKey { get; set; }

    /// <summary>The forge of the owning account.</summary>
    public EForgeProvider? Provider { get; set; }

    [Reference(typeof(Repository), "ApiToken_SelectableRepositories")]
    public List<string> RepositoryIds { get; set; } = [];

    public string? Description { get; set; }

    [Reference(typeof(MintPlayer.Spark.Authorization.Identity.SparkUser))]
    public string CreatedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }
}
