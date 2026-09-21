using CodeCoverage.Forge;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// A GitHub user or organization that owns repositories. Created/updated from
/// GitHub App installation webhooks; document id is Accounts/{GitHubId} so
/// webhook upserts are idempotent.
/// </summary>
[GenerateIndex]
public class Account
{
    /// <summary>Document id of this account, <c>Accounts/{GitHubId}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>GitHub's numeric id for this user or organization; stable even when the login is renamed.</summary>
    public long GitHubId { get; set; }

    /// <summary>The GitHub login (user name or organization slug) of this account.</summary>
    public string Login { get; set; } = string.Empty;

    /// <summary>"User" or "Organization".</summary>
    public string Type { get; set; } = "User";

    /// <summary>URL of the account's GitHub avatar image, shown next to the login.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>GitHub App installation on this account, when the app is installed.</summary>
    public long? InstallationId { get; set; }

    /// <summary>
    /// Account-wide default: delete a merged pull request's head branch, in every repository of
    /// this account that does not decide for itself.
    /// </summary>
    /// <remarks>
    /// A plain <see langword="bool"/> rather than a three-state policy, deliberately: this is the
    /// bottom of the chain, so there is nothing left to defer to. A repository overrides it with
    /// <see cref="EDeleteBranchPolicy.Enabled"/> or <see cref="EDeleteBranchPolicy.Disabled"/>, and
    /// inherits it with <see cref="EDeleteBranchPolicy.Inherit"/> — see
    /// <see cref="Repository.ResolveDeleteBranchOnPrClose"/>, which is the only place the two are
    /// combined.
    /// <para>
    /// Off by default. Deleting a branch is the one irreversible thing this app does to somebody
    /// else's repository, so it is opted into rather than out of.
    /// </para>
    /// </remarks>
    public bool DeleteBranchOnPrClose { get; set; }

    /// <summary>
    /// The forge that hosts this account. Set on every document by the M6 re-key.
    /// </summary>
    /// <remarks>
    /// Stored rather than parsed back out of the id, because callers that hold an entity should not
    /// have to re-derive what the entity already knows — and because the id is a storage detail
    /// while this is a fact about the account.
    /// </remarks>
    public EForgeProvider Provider { get; set; } = EForgeProvider.GitHub;

    /// <summary><see cref="Provider"/> and <c>Login</c> as one comparable value, <c>github:acme</c>.</summary>
    /// <remarks>See <see cref="Repository.OwnerKey"/> for why this is a field of its own.</remarks>
    /// <remarks>Derived — see <see cref="Repository.OwnerKey"/> for why it is not settable.</remarks>
    public string OwnerKey => new Forge.ForgeOwner(Provider, Login ?? string.Empty).ToString();

    /// <summary><c>Accounts/{provider}/{accountId}</c>.</summary>
    /// <remarks>
    /// The provider segment is required because a numeric account id is only unique <em>within</em>
    /// a forge — GitHub user 1234 and GitLab group 1234 are different accounts (D25).
    /// </remarks>
    public static string DocumentId(EForgeProvider provider, long accountId)
        => $"Accounts/{provider.ToCanonicalString()}/{accountId}";
}
