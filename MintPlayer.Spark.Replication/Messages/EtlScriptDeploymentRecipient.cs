using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// Message bus recipient that POSTs ETL script requests to source modules.
/// If the HTTP call fails, an exception is thrown and the message bus retries automatically.
/// </summary>
internal class EtlScriptDeploymentRecipient : IRecipient<EtlScriptDeploymentMessage>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EtlScriptDeploymentRecipient> _logger;

    public EtlScriptDeploymentRecipient(
        IHttpClientFactory httpClientFactory,
        ILogger<EtlScriptDeploymentRecipient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task HandleAsync(EtlScriptDeploymentMessage message, CancellationToken cancellationToken)
    {
        var url = $"{message.SourceModuleUrl.TrimEnd('/')}/spark/etl/deploy";

        _logger.LogInformation(
            "Sending ETL scripts to module '{SourceModule}' at {Url} ({ScriptCount} scripts)",
            message.SourceModuleName, url, message.Request.Scripts.Count);

        var client = _httpClientFactory.CreateClient("spark-etl");
        var response = await client.PostAsJsonAsync(url, message.Request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"ETL deployment to '{message.SourceModuleName}' failed with status {response.StatusCode}: {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<EtlDeploymentResult>(cancellationToken);
        _logger.LogInformation(
            "ETL deployment to '{SourceModule}' succeeded: {Created} created, {Updated} updated, {Removed} removed",
            message.SourceModuleName, result?.TasksCreated, result?.TasksUpdated, result?.TasksRemoved);
    }
}
