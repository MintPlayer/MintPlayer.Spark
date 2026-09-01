using System.Text.Json;
using Microsoft.Extensions.Primitives;
using Octokit.Webhooks;

namespace CodeCoverage.Services;

/// <summary>
/// Dev-only smee.io tunnel used INSTEAD of Spark's AddSmeeDevTunnel.
///
/// GitHub signs the minified body; smee relays it pretty-printed, so the
/// tunnel must re-minify before the HMAC can verify. Spark does that with a
/// Newtonsoft deserialize/serialize round-trip, which doesn't just strip
/// whitespace — it reinterprets scalars: default DateParseHandling rewrites
/// fractional-second timestamps ("…:12.000+02:00" → "…:12+02:00") and float
/// parsing rewrites "1.50" → "1.5". Installation events carry exactly those
/// timestamps, so their signature can never match and they are dropped
/// (measured; repro in docs/spark-handoff.md). This tunnel minifies
/// LEXICALLY instead — whitespace outside strings is removed, no token is
/// ever reinterpreted — which reconstructs GitHub's exact bytes, since JS
/// pretty-printing only ever adds whitespace.
/// </summary>
public sealed class SmeeWebhookTunnelService : BackgroundService
{
    private readonly IConfiguration configuration;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<SmeeWebhookTunnelService> logger;

    public SmeeWebhookTunnelService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        ILogger<SmeeWebhookTunnelService> logger)
    {
        this.configuration = configuration;
        this.httpClientFactory = httpClientFactory;
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channelUrl = configuration["GitHub:SmeeChannelUrl"];
        if (string.IsNullOrEmpty(channelUrl))
            return;

        logger.LogInformation("Connecting to smee.io channel: {ChannelUrl}", channelUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReadEventStream(channelUrl, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "smee.io connection lost — reconnecting in 5s");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ReadEventStream(string channelUrl, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Get, channelUrl);
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var dataLines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                // Blank line terminates one SSE event.
                if (dataLines.Count > 0)
                {
                    var payload = string.Join('\n', dataLines);
                    dataLines.Clear();
                    await HandleFrame(payload, cancellationToken);
                }
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
                dataLines.Add(line[5..].TrimStart(' '));
            // "event:"/"id:"/comment lines carry nothing we need — smee's
            // ready/ping frames are recognized below by their missing body.
        }
    }

    private async Task HandleFrame(string payload, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return; // ready/ping frames aren't always JSON objects
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("body", out var body)
                || !root.TryGetProperty("x-github-event", out _))
                return;

            // Everything except smee's own envelope fields doubles as a header.
            var headers = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("body") || property.NameEquals("query") || property.NameEquals("timestamp"))
                    continue;
                if (property.Value.ValueKind is JsonValueKind.String)
                    headers[property.Name] = new StringValues(property.Value.GetString());
            }

            // The load-bearing part: reconstruct the bytes GitHub signed by
            // stripping only whitespace — never reinterpreting a token.
            var rawBody = LexicalMinify(body.GetRawText());

            using var scope = serviceProvider.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<WebhookEventProcessor>();
            await processor.ProcessWebhookAsync(headers, rawBody, cancellationToken);
        }
    }

    /// <summary>
    /// Removes whitespace outside of string literals; everything inside
    /// strings (including escape sequences) is copied verbatim. The inverse
    /// of pretty-printing, and byte-exact where a JSON parse/re-serialize
    /// is not.
    /// </summary>
    public static string LexicalMinify(string json)
    {
        var result = new System.Text.StringBuilder(json.Length);
        var inString = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (inString)
            {
                result.Append(c);
                if (c == '\\' && i + 1 < json.Length) { result.Append(json[++i]); }
                else if (c == '"') { inString = false; }
            }
            else if (c == '"')
            {
                inString = true;
                result.Append(c);
            }
            else if (!char.IsWhiteSpace(c))
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }
}
