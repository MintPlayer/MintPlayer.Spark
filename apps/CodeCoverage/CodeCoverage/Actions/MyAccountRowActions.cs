using MintPlayer.Spark.Abstractions.Authorization;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Abstractions;
using CodeCoverage.Forge;

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
public partial class MyAccountRowActions : ISparkOwnsRowSecurity
{
    /// <inheritdoc />
    public string RowSecurityRationale =>
        "Rows are GENERATED from the caller's own visibility rather than filtered afterwards: " +
        "IMyAccountsService.GetAsync (Services/MyAccountsService.cs) starts from the owner list " +
        "returned by GitHubAccessService.GetVisibilityAsync (Services/GitHubAccessService.cs), which " +
        "is derived from that user's own GitHub OAuth token, cached per user id, and narrowed to the " +
        "caller's own login or to nothing on every failure path. Nothing outside that list can appear " +
        "in the result, so there is no post-filter to apply. An anonymous caller yields an empty set, " +
        "which is also why security.json grants Query/MyAccountRow to the authenticated role only. " +
        "MyAccountsService is therefore the single line of defence for this type — the framework has " +
        "no backstop behind it, because these rows are not documents.";

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
        var result = await myAccounts.GetAsync(CancellationToken.None, provider: ProviderOf(args.Parent));
        return result.Accounts.AsQueryable();
    }

    /// <summary>
    /// The forge this grid is scoped to, or null for the merged Home page.
    /// </summary>
    /// <remarks>
    /// The same query serves two pages: <c>Home</c>, which fans out across every linked forge, and
    /// <c>ForgeAccounts</c>, which is one forge and says which in its <c>Provider</c> attribute.
    /// Reading the scope off the parent is what keeps that ONE aggregation instead of two that
    /// drift — the bug this service was extracted to prevent in the first place.
    /// <para>
    /// ⚠ An unparseable or absent value yields null, i.e. the merged list. That is safe because
    /// the fan-out is already the caller's own visibility and nothing here widens it: a wrong answer
    /// shows the viewer <em>more of their own accounts</em>, never anyone else's. The failure mode
    /// worth engineering against is the opposite direction, and <c>ForgeAccountsActions</c> handles
    /// it by 404ing an unknown forge before this is ever reached.
    /// </para>
    /// </remarks>
    private static EForgeProvider? ProviderOf(PersistentObject? parent)
    {
        if (parent is null || !string.Equals(parent.Name, "ForgeAccounts", StringComparison.Ordinal))
            return null;

        // Attributes[] rather than the indexer: the indexer THROWS on a missing name, and a page
        // that lost its Provider attribute should fall back to the merged list rather than 500.
        var value = parent.Attributes
            .FirstOrDefault(a => a.Name == "Provider")?.Value?.ToString();
        return ForgeProviders.TryParse(value, out var provider) ? provider : null;
    }
}
