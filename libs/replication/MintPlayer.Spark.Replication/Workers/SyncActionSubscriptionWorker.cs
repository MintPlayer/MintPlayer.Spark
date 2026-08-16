using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Models;
using MintPlayer.Spark.Replication.Services;
using MintPlayer.Spark.SubscriptionWorker;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;
using System.Net;

namespace MintPlayer.Spark.Replication.Workers;

/// <summary>
/// Subscription worker that picks up pending SparkSyncAction documents and POSTs them
/// to the owner module's /spark/sync/apply endpoint.
/// </summary>
internal partial class SyncActionSubscriptionWorker : SparkSubscriptionWorker<SparkSyncAction>
{
    [Inject] private readonly IReplicationHttpClientProvider httpClientProvider;
    [Inject] private readonly IModuleDirectory moduleDirectory;
    private readonly RetryNumerator retryNumerator = new();

    protected override SubscriptionCreationOptions ConfigureSubscription()
    {
        // Two ways in: a brand-new action (no retry scheduled yet), or one whose backoff the
        // sweeper has since declared elapsed.
        //
        // Note WakeUp where the obvious spelling would be `NextAttemptAtUtc <= now()`. That
        // comparison cannot work here and never did (#258): subscriptions are change-vector-driven,
        // so the query runs only when the document is written, which is precisely when a future
        // NextAttemptAtUtc is still in the future. RavenDB 7.2.1 evaluated it to a silent false;
        // 7.2.5 refuses the query outright. SyncActionRetrySweeper evaluates the clock instead and
        // writes the verdict to WakeUp, and that write is what brings the document back for
        // re-evaluation.
        return new SubscriptionCreationOptions
        {
            Query = "from SparkSyncActions where Status = 'Pending' and (NextAttemptAtUtc = null or WakeUp = true)",
        };
    }

    protected override async Task ProcessBatchAsync(
        SubscriptionBatch<SparkSyncAction> batch,
        CancellationToken cancellationToken)
    {
        foreach (var item in batch.Items)
        {
            var syncAction = item.Result;
            var session = batch.OpenAsyncSession();

            try
            {
                syncAction.Status = ESyncActionStatus.Processing;

                // Consume the wake-up — this delivery is what it was asking for. Every exit path
                // below saves, so clearing it here covers success, rejection and retry alike. Left
                // set, an action parked for another attempt would match the subscription again the
                // moment anything wrote to it.
                syncAction.WakeUp = false;

                var ownerUrl = await ResolveModuleUrlAsync(syncAction.OwnerModuleName, cancellationToken);
                var url = $"{ownerUrl.TrimEnd('/')}/spark/sync/apply";

                var request = new SyncActionRequest
                {
                    RequestingModule = syncAction.RequestingModule,
                    Actions = syncAction.Actions,
                };

                Logger.LogInformation(
                    "Sending {ActionCount} sync action(s) to owner module '{OwnerModule}' at {Url}",
                    request.Actions.Count, syncAction.OwnerModuleName, url);

                var client = httpClientProvider.GetClient(syncAction.OwnerModuleName);
                var response = await client.PostAsJsonAsync(url, request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Logger.LogInformation(
                        "Sync actions to '{OwnerModule}' succeeded ({StatusCode})",
                        syncAction.OwnerModuleName, response.StatusCode);

                    syncAction.Status = ESyncActionStatus.Completed;
                    syncAction.NextAttemptAtUtc = null;
                    await retryNumerator.ClearRetryAsync(session, syncAction);
                    await session.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                // Non-retryable errors: mark as permanently failed
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    syncAction.Status = ESyncActionStatus.Failed;
                    syncAction.LastError = $"Rejected with {response.StatusCode}: {body}";
                    syncAction.NextAttemptAtUtc = null;
                    await session.SaveChangesAsync(cancellationToken);

                    Logger.LogError(
                        "Sync action {Id} to '{OwnerModule}' rejected with {StatusCode}: {Body}",
                        syncAction.Id, syncAction.OwnerModuleName, response.StatusCode, body);
                    continue;
                }

                // Retryable errors: use RetryNumerator for backoff
                var error = new HttpRequestException(
                    $"Sync action to '{syncAction.OwnerModuleName}' failed with status {response.StatusCode}: {body}");

                syncAction.LastError = error.Message;
                syncAction.Status = ESyncActionStatus.Pending;
                var retry = await retryNumerator.TrackRetryAsync(session, syncAction, error, Logger);
                syncAction.NextAttemptAtUtc = retry.NextAttemptAtUtc;

                if (!retry.WillRetry)
                {
                    syncAction.Status = ESyncActionStatus.Failed;
                }

                await session.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Error processing sync action {Id}", syncAction.Id);

                syncAction.LastError = ex.Message;
                syncAction.Status = ESyncActionStatus.Pending;
                var retry = await retryNumerator.TrackRetryAsync(session, syncAction, ex, Logger);
                syncAction.NextAttemptAtUtc = retry.NextAttemptAtUtc;

                if (!retry.WillRetry)
                {
                    syncAction.Status = ESyncActionStatus.Failed;
                }

                await session.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task<string> ResolveModuleUrlAsync(string moduleName, CancellationToken cancellationToken)
    {
        var moduleInfo = await moduleDirectory.FindAsync(moduleName, cancellationToken);

        if (moduleInfo == null)
        {
            throw new HttpRequestException(
                $"Owner module '{moduleName}' not found in SparkModules database. Will retry.");
        }

        return moduleInfo.AppUrl;
    }
}
