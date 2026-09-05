using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Ingestion;

/// <summary>
/// Asks GitHub, nightly, what each installation can actually see, and makes our documents agree.
/// <para>
/// Webhooks are the fast path and this is the one that makes the system self-healing. A webhook
/// pipeline has no way back from a delivery it never received: the state is simply wrong from then
/// on, silently and permanently, which is exactly how a repository transferred out of the
/// organization went on being advertised for weeks. Anything that reconciles against the source of
/// truth recovers from a missed delivery on its next run, whatever caused the miss — a dropped
/// event, an outage, a bug in a handler, or a repository that changed while the app was down.
/// </para>
/// </summary>
public partial class ReconcileGitHubStateCronJob : ISparkCronJob
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubStateReconciler reconciler;
    [Inject] private readonly ILogger<ReconcileGitHubStateCronJob> logger;

    /// <summary>03:20 UTC. Nightly is enough: the webhook path handles the timely case, and this
    /// only has to catch what that path lost.</summary>
    public static string CronSchedule => "20 3 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var accounts = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.InstallationId != null)
            .Take(1024)
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
            return;

        var reconciled = 0;
        foreach (var account in accounts)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // One account failing must not cost every account behind it in the list its sweep.
            try
            {
                await reconciler.ReconcileAsync(account, cancellationToken);
                reconciled++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reconciling {Login} failed; the other accounts continue", account.Login);
            }
        }

        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Reconciled {Reconciled} of {Total} installed accounts", reconciled, accounts.Count);
    }
}
