using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;

namespace CodeCoverage.CustomActions;

/// <summary>
/// Drops the cached GitHub visibility for the signed-in caller, so the next read re-queries
/// GitHub for their organizations and App installations. The manual counterpart of the 5-minute
/// TTL, and the same work <c>POST /api/me/accounts/resync</c> does.
/// </summary>
/// <remarks>
/// Operates on the caller, not on rows: <c>selectionRule "=0"</c> in <c>customActions.json</c>,
/// and <c>SelectedItems</c> is never read. It is offered on <c>Home</c> because
/// <c>Resync/Home</c> is the only grant — actions attach by right, not by declaration, and
/// <c>customActions.json</c> is evaluated against every type.
/// <para>
/// The result is a set of client operations rather than a return value: invalidating the cache
/// changes nothing the caller is looking at until the things derived from it are told to
/// re-read. Two things are, and both matter — the accounts grid, and the two counts above it.
/// Refreshing only the grid leaves "Accounts: 2" contradicting the rows underneath it the first
/// time a resync actually changes org membership, which is precisely the case the button exists
/// for.
/// </para>
/// </remarks>
public partial class ResyncAction : SparkCustomAction
{
    [Inject] private readonly IGitHubAccessService gitHubAccess;
    [Inject] private readonly IMyAccountsService myAccounts;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        await gitHubAccess.InvalidateAsync(cancellationToken);

        // Re-read AFTER invalidating — this is the post-resync truth, and it is what the grid
        // is about to fetch for itself.
        var refreshed = await myAccounts.GetAsync(cancellationToken);

        // Parent is the Home page this was invoked from. Null if the action is ever executed
        // without one, in which case there are no counts on screen to correct.
        if (args.Parent is { } home)
        {
            home["AccountCount"].Value = refreshed.Accounts.Length;
            home["RepoCount"].Value = refreshed.Accounts.Sum(a => a.RepoCount);
            manager.Client.RefreshAttribute(home, "AccountCount");
            manager.Client.RefreshAttribute(home, "RepoCount");
        }

        manager.Client.RefreshQuery("my-accounts");
    }
}
