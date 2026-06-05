using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// Message bus recipient that POSTs ETL script requests to source modules.
/// Resolves the source module's URL from the shared <c>SparkModules</c> database
/// on each delivery — never trusts a URL baked into the message — so retries pick
/// up freshly-registered modules instead of repeatedly hitting a stale URL captured
/// at send time. If the source isn't registered yet, throws so the message bus
/// re-queues with backoff until it is.
/// </summary>
internal partial class EtlScriptDeploymentRecipient : IRecipient<EtlScriptDeploymentMessage>
{
    [Inject] private readonly IHttpClientFactory httpClientFactory;
    [Inject] private readonly ModuleRegistrationService registrationService;
    [Inject] private readonly ILogger<EtlScriptDeploymentRecipient> logger;

    public async Task HandleAsync(EtlScriptDeploymentMessage message, CancellationToken cancellationToken)
    {
        var sourceUrl = await ResolveSourceUrlAsync(message.SourceModuleName, cancellationToken);
        var url = $"{sourceUrl.TrimEnd('/')}/spark/etl/deploy";

        logger.LogInformation(
            "Sending ETL scripts to module '{SourceModule}' at {Url} ({ScriptCount} scripts)",
            message.SourceModuleName, url, message.Request.Scripts.Count);

        var client = httpClientFactory.CreateClient("spark-etl");
        var response = await client.PostAsJsonAsync(url, message.Request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"ETL deployment to '{message.SourceModuleName}' failed with status {response.StatusCode}: {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<EtlDeploymentResult>(cancellationToken);
        logger.LogInformation(
            "ETL deployment to '{SourceModule}' succeeded: {Created} created, {Updated} updated, {Removed} removed",
            message.SourceModuleName, result?.TasksCreated, result?.TasksUpdated, result?.TasksRemoved);
    }

    private async Task<string> ResolveSourceUrlAsync(string sourceModuleName, CancellationToken cancellationToken)
    {
        using var modulesStore = registrationService.CreateModulesStore();
        using var session = modulesStore.OpenAsyncSession();
        var sourceInfo = await session.LoadAsync<ModuleInformation>(
            $"moduleInformations/{sourceModuleName}", cancellationToken);

        if (sourceInfo is null || string.IsNullOrEmpty(sourceInfo.AppUrl))
        {
            // Not yet registered → throw so the message bus re-queues with backoff.
            // Once the source module starts up and registers (or rotates its URL),
            // the next retry resolves through to the fresh value.
            throw new InvalidOperationException(
                $"Source module '{sourceModuleName}' is not registered in SparkModules; " +
                "ETL deployment will retry once it registers.");
        }

        return sourceInfo.AppUrl;
    }
}
