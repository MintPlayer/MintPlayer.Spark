using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Queries;

namespace CodeCoverage.Actions;

/// <summary>
/// Backs the <c>Custom.MyAccounts</c> query of the virtual <c>MyAccountRow</c> type.
/// <para>
/// No base class and no <c>&lt;T&gt;</c>: there is no CLR entity here, and since preview.67 a
/// composed query on a <c>clrType</c>-less type needs none. The rows are an aggregate over Account
/// and Repository documents, computed per request.
/// </para>
/// <para>
/// No <c>GetRowFilterAsync</c> either, and that is not an omission. A row filter is an
/// <c>Expression&lt;Func&lt;TEntity,bool&gt;&gt;</c> over documents; these rows are not documents.
/// The scoping instead comes from where the rows come from at all —
/// <see cref="IMyAccountsService"/> starts from the caller's GitHub-verified owners, so a caller
/// can only ever be handed their own. An anonymous caller gets none, which is also why
/// <c>security.json</c> grants <c>Query/MyAccountRow</c> to the authenticated role only.
/// </para>
/// </summary>
public partial class MyAccountRowActions
{
    [Inject] private readonly IMyAccountsService myAccounts;

    /// <summary>
    /// The signed-in caller's accounts. Returned as an in-memory queryable, so the query's
    /// <c>sortColumns</c> may name any property on the row — nothing here reaches an index.
    /// </summary>
    /// <remarks>
    /// Every row's <c>Id</c> is its <c>Login</c>. The projector throws by name on a null or
    /// duplicate row id rather than collapsing the grid to one row, and logins are already unique
    /// per account, so this needs no separate identity.
    /// </remarks>
    public async Task<IQueryable<MyAccountRow>> MyAccounts(CustomQueryArgs args)
    {
        var result = await myAccounts.GetAsync(CancellationToken.None);
        return result.Accounts.AsQueryable();
    }
}
