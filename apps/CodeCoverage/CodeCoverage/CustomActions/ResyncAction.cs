using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;
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
    [Inject] private readonly IGitHubStateReconciler reconciler;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ILogger<ResyncAction> logger;
    [Inject] private readonly IManager manager;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        await gitHubAccess.InvalidateAsync(cancellationToken);

        // Dropping the cache re-reads which owners the caller can see, but says nothing about what
        // each installation now holds — and a repository that was transferred away is still sitting
        // in our documents looking perfectly current. So the button also reconciles, which is the
        // same work the nightly job does, scoped to the accounts this caller actually manages. It
        // is what makes the button repair what the person pressing it is looking at.
        var owners = await gitHubAccess.GetAllowedOwnersAsync(cancellationToken);
        if (owners.Length > 0)
        {
            var accounts = await session.Query<Account, Indexes.Accounts_Overview>()
                .Where(a => a.Login.In(owners) && a.InstallationId != null)
                .ToListAsync(cancellationToken);

            foreach (var account in accounts)
            {
                try
                {
                    await reconciler.ReconcileAsync(account, cancellationToken);
                }
                catch (Exception ex)
                {
                    // The button's job is to refresh the view; a GitHub hiccup on one account must
                    // not turn that into an error page.
                    logger.LogWarning(ex, "Resync could not reconcile {Login}", account.Login);
                }
            }

            try
            {
                await session.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // Inside the boundary for the same reason the loop above is: a write conflict on
                // one account would otherwise discard every other account's reconcile AND 500 the
                // button, which is the opposite of what the per-account catch is there to promise.
                logger.LogWarning(ex, "Resync reconciled accounts but could not save the result");
                manager.Client.Notify(
                    "Some accounts could not be updated. Try again in a moment.", NotificationKind.Warning);
            }
        }

        // Re-read AFTER invalidating and reconciling — this is the post-resync truth, and it is
        // what the grid is about to fetch for itself.
        //
        // Non-stale: the reconcile above just wrote to the very indexes this reads, and RavenDB
        // indexes are eventually consistent. Without the wait this returns the pre-resync numbers
        // on exactly the click that changed something — the button's whole visible output, wrong.
        var refreshed = await myAccounts.GetAsync(cancellationToken, waitForNonStaleResults: true);

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
