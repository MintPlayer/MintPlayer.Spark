using System.Linq.Expressions;
using Raven.Client.Documents.Linq;
using MintPlayer.Spark.Abstractions.Authorization;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.Actions;

/// <summary>
/// Accounts are public to READ (GitHub logins/avatars) and manager-only to WRITE. The installation
/// id is operational detail only the account's managers get, whatever the action.
/// </summary>
public partial class AccountActions : DefaultPersistentObjectActions<Account>, ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Unfiltered for READS, filtered for WRITES. An Account is a GitHub login and avatar — data " +
        "GitHub already publishes — and coverage.mintplayer.com is a public dashboard, so " +
        "security.json grants QueryRead/Account to the anonymous role on purpose. Edit/Account is " +
        "granted too, so an owner can set the account-wide DeleteBranchOnPrClose default, and that " +
        "makes the write path reachable: without a filter, RowSecurity treats a missing rule as " +
        "ALLOWED, so any signed-in user could edit any account. Writes are therefore narrowed to " +
        "accounts the caller manages. What is never public is the installation id, withheld per row " +
        "by GetProtectedAttributesAsync rather than by hiding the row.";

    [Inject] private readonly ISparkVisibility visibility;

    /// <summary>
    /// No filter for reads; managers only for writes.
    /// </summary>
    /// <remarks>
    /// ⚠️ Returning <see langword="null"/> means UNRESTRICTED, not "denied" — <c>RowSecurity</c>
    /// evaluates a missing rule as allowed. That is correct for the public read surface and would be
    /// a hole on the write one, which is why the two are answered separately here rather than by one
    /// expression.
    /// </remarks>
    public override async Task<Expression<Func<Account, bool>>?> GetRowFilterAsync(string action)
    {
        if (action is "Query" or "Read")
            return null;

        // `.In()` rather than Contains: this predicate is pushed into RQL, where Contains is
        // untranslatable — see ApiTokenActions for the production outage that taught us.
        var owners = await visibility.GetAllowedOwnersAsync();
        return account => account.Login.In(owners);
    }

    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Account entity)
        => await visibility.CanManageOwnerAsync(entity.Login)
            ? null
            : [nameof(Account.InstallationId)];
}
