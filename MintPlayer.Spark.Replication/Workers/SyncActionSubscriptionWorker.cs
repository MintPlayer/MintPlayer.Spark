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
internal class SyncActionSubscriptionWorker : SparkSubscriptionWorker<SparkSyncAction>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ModuleRegistrationService _registrationService;
    private readonly RetryNumerator _retryNumerator = new();

    public SyncActionSubscriptionWorker(
        IDocumentStore store,
        IHttpClientFactory httpClientFactory,
        ModuleRegistrationService registrationService,
        ILogger<SyncActionSubscriptionWorker> logger)
        : base(store, logger)
    {
        _httpClientFactory = httpClientFactory;
        _registrationService = registrationService;
    }

    protected override SubscriptionCreationOptions ConfigureSubscription()
    {
        return new SubscriptionCreationOptions
        {
            Query = "from SparkSyncActions where Status = 'Pending'",
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

                var client = _httpClientFactory.CreateClient("spark-sync");
                var response = await client.PostAsJsonAsync(url, request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Logger.LogInformation(
                        "Sync actions to '{OwnerModule}' succeeded ({StatusCode})",
                        syncAction.OwnerModuleName, response.StatusCode);

                    syncAction.Status = ESyncActionStatus.Completed;
                    await _retryNumerator.ClearRetryAsync(session, syncAction);
                    await session.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                // Non-retryable errors: mark as permanently failed
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    syncAction.Status = ESyncActionStatus.Failed;
                    syncAction.LastError = $"Rejected with {response.StatusCode}: {body}";
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
                var willRetry = await _retryNumerator.TrackRetryAsync(session, syncAction, error, Logger);

                if (!willRetry)
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
                var willRetry = await _retryNumerator.TrackRetryAsync(session, syncAction, ex, Logger);

                if (!willRetry)
                {
                    syncAction.Status = ESyncActionStatus.Failed;
                }

                await session.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task<string> ResolveModuleUrlAsync(string moduleName, CancellationToken cancellationToken)
    {
        using var modulesStore = _registrationService.CreateModulesStore();
        using var session = modulesStore.OpenAsyncSession();

        var moduleId = $"moduleInformations/{moduleName}";
        var moduleInfo = await session.LoadAsync<ModuleInformation>(moduleId, cancellationToken);

        if (moduleInfo == null)
        {
            throw new HttpRequestException(
                $"Owner module '{moduleName}' not found in SparkModules database. Will retry.");
        }

        return moduleInfo.AppUrl;
    }
}
