using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;
using Newtonsoft.Json;
using Smee.IO.Client;
using Smee.IO.Client.Dto;

namespace MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;

internal class SmeeBackgroundService : BackgroundService
{
    private readonly SmeeOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SmeeBackgroundService> _logger;

    public SmeeBackgroundService(
        IOptions<SmeeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<SmeeBackgroundService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_options.ChannelUrl))
            return;

        _logger.LogInformation("Connecting to smee.io channel: {ChannelUrl}", _options.ChannelUrl);

        var smeeClient = new SmeeClient(new Uri(_options.ChannelUrl));
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

            var httpClient = _httpClientFactory.CreateClient("SparkSmeeDevTunnel");

            var request = new HttpRequestMessage(HttpMethod.Post, _options.LocalWebhookPath);
            request.Content = new StringContent(minifiedBody, System.Text.Encoding.UTF8, "application/json");

            // Forward all GitHub headers from the smee event
            foreach (var header in e.Data.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            await httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to forward smee.io webhook to local endpoint");
        }
    }
}
