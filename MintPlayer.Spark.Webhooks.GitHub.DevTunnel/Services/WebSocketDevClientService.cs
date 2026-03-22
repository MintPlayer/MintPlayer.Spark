using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.Webhooks;
using System.Net.WebSockets;

namespace MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;

internal class WebSocketDevClientService : BackgroundService
{
    private readonly WebSocketDevTunnelOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WebSocketDevClientService> _logger;

    public WebSocketDevClientService(
        IOptions<WebSocketDevTunnelOptions> options,
        IServiceProvider serviceProvider,
        ILogger<WebSocketDevClientService> logger)
    {
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_options.ProductionWebSocketUrl))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceive(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSocket dev tunnel connection lost — reconnecting in 5 seconds");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ConnectAndReceive(CancellationToken stoppingToken)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(900);
        ws.Options.AddSubProtocol("ws");
        ws.Options.AddSubProtocol("wss");

        var baseUri = new Uri(_options.ProductionWebSocketUrl);
        _logger.LogInformation("Connecting to production WebSocket: {Url}", baseUri);

        await ws.ConnectAsync(baseUri, stoppingToken);
        _logger.LogInformation("Connected to production WebSocket");

        // Send handshake with GitHub token
        var handshake = new Handshake { GithubToken = _options.GitHubToken };
        await ws.WriteObject(handshake);

        while (!stoppingToken.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var message = await ws.ReadMessage();

            // Parse headers\n\nbody wire format
            var separatorIndex = message.IndexOf("\n\n", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                _logger.LogWarning("Received malformed WebSocket message — missing header/body separator");
                continue;
            }

            var headerBlock = message[..separatorIndex];
            var body = message[(separatorIndex + 2)..];
            var headers = headerBlock.Split('\n')
                .Select(h => h.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(
                    parts => parts[0].Trim(),
                    parts => new StringValues(parts[1].Trim()));

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<WebhookEventProcessor>();
                await processor.ProcessWebhookAsync(headers, body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process WebSocket webhook");
            }
        }
    }
}
