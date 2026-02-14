using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// Message bus recipient that resolves the owner module's URL and POSTs sync actions to it.
/// Distinguishes retryable vs non-retryable errors to avoid wasting retry attempts.
/// </summary>
internal class SyncActionDeploymentRecipient : IRecipient<SyncActionDeploymentMessage>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ModuleRegistrationService _registrationService;
    private readonly ILogger<SyncActionDeploymentRecipient> _logger;

    public SyncActionDeploymentRecipient(
        IHttpClientFactory httpClientFactory,
        ModuleRegistrationService registrationService,
        ILogger<SyncActionDeploymentRecipient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _registrationService = registrationService;
        _logger = logger;
    }

    public async Task HandleAsync(SyncActionDeploymentMessage message, CancellationToken cancellationToken)
    {
        // Resolve owner module URL from SparkModules database
        var ownerUrl = await ResolveModuleUrlAsync(message.OwnerModuleName, cancellationToken);

        var url = $"{ownerUrl.TrimEnd('/')}/spark/sync/apply";

        _logger.LogInformation(
            "Sending {ActionCount} sync action(s) to owner module '{OwnerModule}' at {Url}",
            message.Request.Actions.Count, message.OwnerModuleName, url);

        var client = _httpClientFactory.CreateClient("spark-sync");
        var response = await client.PostAsJsonAsync(url, message.Request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation(
                "Sync actions to '{OwnerModule}' succeeded ({StatusCode})",
                message.OwnerModuleName, response.StatusCode);
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        // Non-retryable errors: dead-letter immediately by throwing a specific exception
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            throw new NonRetryableException(
                $"Sync action to '{message.OwnerModuleName}' rejected with {response.StatusCode}: {body}");
        }

        // Retryable errors: throw standard exception so the message bus retries
        throw new HttpRequestException(
            $"Sync action to '{message.OwnerModuleName}' failed with status {response.StatusCode}: {body}");
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
