using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.Webhooks;
using System.Net.WebSockets;

namespace MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;

internal partial class WebSocketDevClientService : BackgroundService
{
    [Options] private readonly IOptions<WebSocketDevTunnelOptions> _options;
    [Inject] private readonly IServiceProvider _serviceProvider;
    [Inject] private readonly ILogger<WebSocketDevClientService> _logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_options.Value.ProductionWebSocketUrl))
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

    /// <summary>
    /// R2-L7: refuse to send a GitHub PAT over plain <c>ws://</c> to a non-loopback host.
    /// <para>
    /// The handshake's first frame carries the token, so an operator who points
    /// <c>ProductionWebSocketUrl</c> at a misconfigured <c>ws://example.test</c> leaks a live
    /// credential in cleartext to whatever is on the wire — and gets no error, because the tunnel
    /// would otherwise connect perfectly happily.
    /// </para>
    /// <para>
    /// Extracted from <see cref="ConnectAndReceive"/> so that it can be tested. It was previously
    /// inline in a <see cref="BackgroundService"/> loop that swallows exceptions and retries every
    /// five seconds, which means the guard could have been deleted or inverted and the only
    /// symptom would have been a log line nobody reads.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Loopback over plain ws is deliberately allowed: that is the normal local development case,
    /// the traffic never leaves the machine, and requiring a certificate for localhost would push
    /// people towards disabling the check altogether.
    /// </remarks>
    internal static void EnsureTokenWillNotTravelInCleartext(Uri baseUri)
    {
        if (string.Equals(baseUri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) && !baseUri.IsLoopback)
        {
            throw new InvalidOperationException(
                $"DevTunnel ProductionWebSocketUrl '{baseUri}' uses plain ws:// to a non-loopback host. " +
                "The handshake carries the GitHub PAT — refuse to send it in cleartext. Use wss:// or ws://localhost.");
        }
    }

    private async Task ConnectAndReceive(CancellationToken stoppingToken)
    {
        // Disposed on every exit. It used to be abandoned, so each reconnect leaked the previous
        // socket — its connection never torn down, the server's close never completed.
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(900);
        ws.Options.AddSubProtocol("ws");
        ws.Options.AddSubProtocol("wss");

        var baseUri = new Uri(_options.Value.ProductionWebSocketUrl);

        EnsureTokenWillNotTravelInCleartext(baseUri);

        _logger.LogInformation("Connecting to production WebSocket: {Url}", baseUri);

        await ws.ConnectAsync(baseUri, stoppingToken);
        _logger.LogInformation("Connected to production WebSocket");

        // Send handshake with GitHub token
        var handshake = new Handshake { GithubToken = _options.Value.GitHubToken };
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

            try
            {
                var headers = ParseHeaders(headerBlock);

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

    /// <summary>
    /// Parses the <c>Name: value</c> lines of a forwarded delivery. A name that appears more than
    /// once keeps every value, as HTTP does, and names compare case-insensitively.
    /// <para>
    /// This used to be a <c>ToDictionary</c>, which throws on a repeated name — outside the
    /// per-message <c>try</c>, so one such delivery dropped the connection, and with it every
    /// delivery sent during the five seconds before the reconnect.
    /// </para>
    /// </summary>
    private static Dictionary<string, StringValues> ParseHeaders(string headerBlock)
    {
        var headers = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headerBlock.Split('\n'))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2)
                continue;

            var name = parts[0].Trim();
            headers[name] = headers.TryGetValue(name, out var existing)
                ? StringValues.Concat(existing, parts[1].Trim())
                : new StringValues(parts[1].Trim());
        }

        return headers;
    }
}
