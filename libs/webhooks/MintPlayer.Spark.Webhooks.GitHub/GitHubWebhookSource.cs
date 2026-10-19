using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.Webhooks;

namespace MintPlayer.Spark.Webhooks.GitHub;

/// <summary>GitHub as a source of <c>MintPlayer.Spark.Webhooks</c>.</summary>
public static class GitHubWebhookSource
{
    /// <summary>The source name.</summary>
    public const string Name = "github";

    /// <summary>GitHub caps a delivery at 25 MB and drops larger payloads itself.</summary>
    public const long MaxBodyBytes = 25L * 1024 * 1024;

    /// <summary>
    /// The source definition: <c>X-Hub-Signature-256</c> with the secret read from
    /// <c>IOptions&lt;GitHubWebhooksOptions&gt;</c> per delivery, JSON only, its own route, and the
    /// processor run inline (it enqueues and deduplicates on <c>X-GitHub-Delivery</c> itself).
    /// </summary>
    internal static void Configure(WebhookSourceBuilder source)
    {
        source.SharedRoute = false;
        source.MaxBodyBytes = MaxBodyBytes;
        source.ContentTypes.Add("application/json");
        source.Verification = services => GitHubWebhookSignature.Create(
            services.GetRequiredService<IOptions<GitHubWebhooksOptions>>().Value.WebhookSecret);
        source.DeliveryId = WebhookDeliveryIds.FromHeader("X-GitHub-Delivery");
        source.UseHandler<GitHubWebhookHandler>(WebhookHandling.Inline);
    }
}

/// <summary>
/// Hands a verified GitHub delivery to <c>SparkWebhookEventProcessor</c>, which
/// forwards development-app deliveries to the dev tunnel, and otherwise broadcasts the catch-all
/// envelope once per <c>X-GitHub-Delivery</c> and the typed envelopes. Inline, so the enqueue happens
/// before GitHub gets its 200 (the processor's W1).
/// </summary>
internal sealed partial class GitHubWebhookHandler : IWebhookHandler
{
    [Inject] private readonly WebhookEventProcessor processor;

    public async Task HandleAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var headers = delivery.Headers.ToDictionary(
            h => h.Key,
            h => new StringValues(h.Value),
            StringComparer.OrdinalIgnoreCase);

        // Text for Octokit's API, decoded from the exact bytes. The processor checks the signature
        // again over these characters; for the UTF-8 JSON GitHub sends, re-encoding gives back the
        // same bytes.
        await processor.ProcessWebhookAsync(headers, delivery.ReadBodyText(), cancellationToken);
    }
}
