using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark;
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
public partial class ReconcileForgeStateCronJob : ISparkCronJob
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly ILogger<ReconcileForgeStateCronJob> logger;

    /// <summary>03:20 UTC. Nightly is enough: the webhook path handles the timely case, and this
    /// only has to catch what that path lost.</summary>
    public static string CronSchedule => "20 3 * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // ⚠️ Selected on the NEUTRAL field, not on `InstallationId`. An installation is GitHub's
        // mechanism for granting access, and querying it here made the nightly sweep skip every
        // account on any other forge — silently, since an empty result is indistinguishable from
        // nothing needing work.
        //
        // `!= Disconnected` rather than `== Connected`: an absent field does not satisfy an equality
        // in RavenDB, so equality would skip every account written before the field existed.
        var accounts = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.Connection != RepositoryConnection.Disconnected)
            .Take(1024)
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
            return;

        // One save per account (below) plus the reconciles' own reads: bounded by the Take above,
        // not by the default per-session budget of 30.
        using var requestScope = session.IgnoreMaxRequests();

        var reconciled = 0;
        foreach (var listed in accounts)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Reloaded rather than used as listed. A tracked id is served from the session without a
            // request; after a failed account has cleared the session (below), it is fetched again,
            // because an entity from before the clear is no longer tracked and its changes would
            // never be saved.
            var account = await session.LoadAsync<Account>(listed.Id, cancellationToken);
            if (account is null) continue;

            // One account failing must not cost every account behind it in the list its sweep.
            try
            {
                await forges.For(account.Provider).ReconcileAsync(account, cancellationToken);

                // Each account is its own unit of work. A single save after the loop persisted
                // whatever a failing reconcile had written into the session before it threw — a
                // half-applied reconcile, saved as though it were a whole one.
                await session.SaveChangesAsync(cancellationToken);
                reconciled++;
            }
            catch (Exception ex)
            {
                // Everything pending belongs to this account alone, since every earlier one was
                // saved; clearing discards exactly its partial changes.
                session.Advanced.Clear();
                logger.LogWarning(ex, "Reconciling {Login} failed; the other accounts continue", account.Login);
            }
        }

        logger.LogInformation("Reconciled {Reconciled} of {Total} installed accounts", reconciled, accounts.Count);
    }
}
