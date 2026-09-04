using System.Net;
using System.Net.Http.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// Delivers a replication write to the module that owns the collection.
/// </summary>
/// <remarks>
/// The body is the deleted <c>SyncActionSubscriptionWorker</c>'s, minus everything messaging now
/// provides: no status enum, no wake-up gate, no sweeper, no retry numerator. What remains is the
/// part that was ever specific to replication — resolve the owner's URL, POST over mTLS, and decide
/// which HTTP results are worth retrying.
/// </remarks>
[Register(typeof(IRecipient<SyncActionMessage>), ServiceLifetime.Scoped)]
internal sealed partial class SyncActionRecipient : IRecipient<SyncActionMessage>
{
    [Inject] private readonly IModuleDirectory moduleDirectory;
    [Inject] private readonly IReplicationHttpClientProvider httpClientProvider;
    [Inject] private readonly ILogger<SyncActionRecipient> logger;

    public async Task HandleAsync(SyncActionMessage message, CancellationToken cancellationToken = default)
    {
        var ownerUrl = await ResolveModuleUrlAsync(message.OwnerModuleName, cancellationToken);
        var url = $"{ownerUrl.TrimEnd('/')}/spark/sync/apply";

        var request = new SyncActionRequest
        {
            RequestingModule = message.RequestingModule,
            Actions = message.Actions,
        };

        logger.LogInformation(
            "Sending {ActionCount} sync action(s) to owner module '{OwnerModule}' at {Url}",
            request.Actions.Count, message.OwnerModuleName, url);

        // mTLS: the client certificate is what proves to the owner which module is calling, and the
        // owner gates on RequestingModule matching it. Resolving the client per module is therefore
        // part of the security contract, not a convenience.
        var client = httpClientProvider.GetClient(message.OwnerModuleName);
        var response = await client.PostAsJsonAsync(url, request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "Sync actions to '{OwnerModule}' succeeded ({StatusCode})",
                message.OwnerModuleName, response.StatusCode);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // 400 and 404 are the owner saying "this will never be accepted" — a malformed action, or a
        // collection it does not own. Retrying cannot change either, so this is exactly what
        // NonRetryableException expresses, and the handler dead-letters immediately instead of
        // climbing a ladder to the same conclusion.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            throw new NonRetryableException(
                $"Sync action to '{message.OwnerModuleName}' was rejected with {response.StatusCode}: {body}");
        }

        // Anything else — 5xx, a timeout, the owner still starting — is worth another attempt, so it
        // is thrown as an ordinary exception and the lane's retry schedule decides.
        throw new HttpRequestException(
            $"Sync action to '{message.OwnerModuleName}' failed with status {response.StatusCode}: {body}");
    }

    private async Task<string> ResolveModuleUrlAsync(string moduleName, CancellationToken cancellationToken)
    {
        var module = await moduleDirectory.FindAsync(moduleName, cancellationToken);

        // A module missing from the directory may simply not have registered itself yet; that is
        // transient, so it is retryable rather than terminal.
        return module?.AppUrl
            ?? throw new HttpRequestException(
                $"Owner module '{moduleName}' not found in the SparkModules database. Will retry.");
    }
}
