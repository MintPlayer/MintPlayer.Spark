using MintPlayer.Spark.Abstractions.Authorization;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>
/// Accounts are public data (GitHub logins/avatars), so there is no row filter —
/// but the installation id is operational detail only the account's managers get.
/// </summary>
public partial class AccountActions : DefaultPersistentObjectActions<Account>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Deliberately unfiltered. An Account is a GitHub login and avatar — data GitHub already " +
        "publishes — and coverage.mintplayer.com is a public dashboard, so security.json grants " +
        "QueryRead/Account to the anonymous role on purpose. What is NOT public is the installation " +
        "id, and that is withheld per row by GetProtectedAttributesAsync below rather than by hiding " +
        "the row. Contrast Repository, which does carry a row filter (RepositoryVisibility), because " +
        "a private repository's existence is not public.";

    [Inject] private readonly ISparkVisibility visibility;

    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Account entity)
        => await visibility.CanManageOwnerAsync(entity.Login)
            ? null
            : [nameof(Account.InstallationId)];
}
