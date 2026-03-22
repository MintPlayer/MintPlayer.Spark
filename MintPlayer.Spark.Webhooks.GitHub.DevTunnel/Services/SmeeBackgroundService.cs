using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;
using Newtonsoft.Json;
using Octokit.Webhooks;
using Smee.IO.Client;
using Smee.IO.Client.Dto;

namespace MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;

internal partial class SmeeBackgroundService : BackgroundService
{
    [Options] private readonly IOptions<SmeeOptions> _options;
    [Inject] private readonly IServiceProvider _serviceProvider;
    [Inject] private readonly ILogger<SmeeBackgroundService> _logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_options.Value.ChannelUrl))
            return;

        _logger.LogInformation("Connecting to smee.io channel: {ChannelUrl}", _options.Value.ChannelUrl);

        var smeeClient = new SmeeClient(new Uri(_options.Value.ChannelUrl));
        smeeClient.OnMessage += SmeeClient_OnMessage;

        try
        {
            // StartAsync is a blocking call
            await smeeClient.StartAsync(stoppingToken);
        }
        finally
        {
            smeeClient.Stop();
            smeeClient.OnMessage -= SmeeClient_OnMessage;
        }
    }

    private async void SmeeClient_OnMessage(object? sender, SmeeEvent e)
    {
        if (e.Event != SmeeEventType.Message)
            return;

        try
        {
            // Re-minimize JSON to match the exact bytes GitHub signed
            // (smee.io may reformat/pretty-print the JSON during SSE relay)
            var minifiedBody = JsonConvert.SerializeObject(
                JsonConvert.DeserializeObject(e.Data.Body.ToString()
                    ?? throw new InvalidOperationException("Smee body cannot be empty")));

            var headers = e.Data.Headers
                .ToDictionary(h => h.Key, h => new StringValues(h.Value));

            using var scope = _serviceProvider.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<WebhookEventProcessor>();
            await processor.ProcessWebhookAsync(headers, minifiedBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process smee.io webhook");
        }
    }
}
