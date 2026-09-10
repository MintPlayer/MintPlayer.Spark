using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;
using Octokit.Webhooks;

namespace MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;

/// <summary>
/// Relays a smee.io channel into the local webhook processor over Server-Sent Events.
/// <para>
/// This reads the SSE stream directly rather than through a client library, because the body must
/// reach <see cref="WebhookEventProcessor"/> as the exact bytes GitHub signed — see
/// <see cref="SmeeBodyReader"/> for why no library that hands back a parsed body can be used here.
/// </para>
/// </summary>
internal partial class SmeeBackgroundService : BackgroundService
{
    [Options] private readonly IOptions<SmeeOptions> _options;
    [Inject] private readonly IHttpClientFactory _httpClientFactory;
    [Inject] private readonly IServiceProvider _serviceProvider;
    [Inject] private readonly ILogger<SmeeBackgroundService> _logger;

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channelUrl = _options.Value.ChannelUrl;
        if (string.IsNullOrEmpty(channelUrl))
            return;

        _logger.LogInformation("Connecting to smee.io channel: {ChannelUrl}", channelUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReadEventStream(channelUrl, stoppingToken);

                // A clean end of stream is smee closing the connection; reconnect like a fault.
                _logger.LogInformation("smee.io stream ended — reconnecting in {Delay}", ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown. Returning here keeps the exception out of ExecuteAsync, which
                // would otherwise surface as an unhandled TaskCanceledException on host stop.
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "smee.io connection lost — reconnecting in {Delay}", ReconnectDelay);

                try
                {
                    await Task.Delay(ReconnectDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ReadEventStream(string channelUrl, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(SmeeBackgroundService));
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Get, channelUrl);
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        await foreach (var frame in SmeeSseReader.ReadFramesAsync(stream, cancellationToken))
        {
            if (!SmeeBodyReader.TryReadDelivery(frame, out var headers, out var body))
                continue;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<WebhookEventProcessor>();
                await processor.ProcessWebhookAsync(headers, body, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One malformed delivery must not tear down the tunnel.
                _logger.LogError(ex, "Failed to process a smee.io webhook delivery");
            }
        }
    }
}
