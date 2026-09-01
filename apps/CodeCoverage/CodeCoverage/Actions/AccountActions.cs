using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>
/// Accounts are public data (GitHub logins/avatars), so there is no row filter —
/// but the installation id is operational detail only the account's managers get.
/// </summary>
public partial class AccountActions : DefaultPersistentObjectActions<Account>
{
    [Inject] private readonly ISparkVisibility visibility;

    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Account entity)
        => await visibility.CanManageOwnerAsync(entity.Login)
            ? null
            : [nameof(Account.InstallationId)];
}
