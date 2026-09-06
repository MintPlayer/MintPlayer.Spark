using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.CheckRun;
using Octokit.Webhooks.Events.CheckSuite;
using Octokit.Webhooks.Events.Installation;
using Octokit.Webhooks.Events.InstallationRepositories;
using Octokit.Webhooks.Events.InstallationTarget;
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.Issues;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using Octokit.Webhooks.Events.PullRequestReviewComment;
using Octokit.Webhooks.Events.Organization;
using Octokit.Webhooks.Events.Repository;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

[Register(typeof(WebhookEventProcessor), ServiceLifetime.Scoped)]
internal partial class SparkWebhookEventProcessor : WebhookEventProcessor
{
    [Inject] private readonly IMessageBus _messageBus;
    [Inject] private readonly IMessageRecipientRegistry _recipients;
    [Options] private readonly IOptions<GitHubWebhooksOptions> _options;
    [Inject] private readonly ISignatureService _signatureService;
    [Inject] private readonly IServiceProvider _serviceProvider;
    [Inject] private readonly IHostEnvironment _hostEnvironment;
    [Inject] private readonly ILogger<SparkWebhookEventProcessor> _logger;

    // Stashed per-request for catch-all message and dev forwarding
    private string? _rawBody;
    private IDictionary<string, StringValues>? _rawHeaders;

    public override async ValueTask ProcessWebhookAsync(IDictionary<string, StringValues> headers, string body, CancellationToken cancellationToken = default)
    {
        // Octokit uses case-sensitive dictionary — make it case-insensitive
        var caseInsensitiveHeaders = new Dictionary<string, StringValues>(headers, StringComparer.OrdinalIgnoreCase);

        // Validate webhook signature. SignatureService is fail-closed on empty
        // secret AND on signature mismatch, so unconditionally route every delivery
        // through it — an outer "if secret configured" gate would re-open the
        // fail-open hole the service is meant to plug.
        caseInsensitiveHeaders.TryGetValue("X-Hub-Signature-256", out var signatureSha256);
        if (!_signatureService.VerifySignature(signatureSha256, _options.Value.WebhookSecret, body))
        {
            if (string.IsNullOrEmpty(_options.Value.WebhookSecret))
                _logger.LogError("GitHub webhook signature validation failed — WebhookSecret is empty. Configure GitHub:WebhookSecret in appsettings/user secrets/env.");
            else
                _logger.LogWarning("GitHub webhook signature validation failed — dropping event");
            return;
        }

        // Check if this is from the development GitHub App
        if (_options.Value.DevelopmentAppId.HasValue)
        {
            caseInsensitiveHeaders.TryGetValue("X-GitHub-Hook-Installation-Target-ID", out var targetId);
            if (long.TryParse(targetId.ToString(), out var appId) && appId == _options.Value.DevelopmentAppId.Value)
            {
                // Forward to connected dev clients instead of processing locally
                var devSocketService = _serviceProvider.GetService<IDevWebSocketService>();
                if (devSocketService != null)
                {
                    if (_hostEnvironment.IsDevelopment())
                        _logger.LogWarning(
                            """
                            Received webhook, downstreaming to connected dev clients.
                            Since GitHub:Development:AppId is configured, the webhook is NOT passed to the local WebhookEventProcessor.
                            If you want to handle webhooks here, remove the GitHub:Development:AppId configuration value.
                            """);

                    await devSocketService.SendToClients(caseInsensitiveHeaders, body);
                }
                return;
            }
        }

        // Stash for use in specific event handlers
        _rawHeaders = caseInsensitiveHeaders;
        _rawBody = body;

        // The catch-all is broadcast HERE, for every delivery, rather than from the typed
        // overrides below. Octokit dispatches by overriding one method per event and its base
        // implementations are no-ops, so an event nobody overrode is silently discarded — which is
        // exactly how `installation_repositories` came to be dropped for the lifetime of the
        // library while an app sat waiting for it. There is no list to keep in step here: whatever
        // GitHub sends, a recipient of the catch-all sees.
        await BroadcastCatchAllAsync(caseInsensitiveHeaders, body, cancellationToken);

        // Typed dispatch is best-effort, and must not be able to fail the delivery.
        //
        // Octokit deserializes into an action-specific type with required properties, so a payload
        // shape it does not model — a new action, a field GitHub added, an event whose schema moved
        // — throws. Before the catch-all existed that failure at least meant "nothing was
        // delivered"; now the catch-all has already gone out, so letting it propagate would return
        // 500 to GitHub, earn a redelivery, and broadcast the catch-all a second time. The event
        // would be handled twice for the sake of an envelope nobody could deserialize anyway.
        try
        {
            await base.ProcessWebhookAsync(caseInsensitiveHeaders, body, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not build a typed envelope for GitHub event '{EventType}'; the catch-all envelope was delivered.",
                Header(caseInsensitiveHeaders, "X-GitHub-Event"));
        }
    }

    /// <summary>
    /// Broadcasts the untyped envelope for any event, typed or not. The installation id and
    /// repository name are read straight out of the payload rather than off a deserialized event,
    /// because most GitHub events have no Octokit type here and would otherwise arrive with both
    /// fields blank — and every consumer routes on them.
    /// </summary>
    private async ValueTask BroadcastCatchAllAsync(
        IDictionary<string, StringValues> headers, string body, CancellationToken cancellationToken)
    {
        if (!_recipients.HasRecipient<GitHubWebhookMessage>())
            return;

        var (installationId, repositoryFullName) = ReadRoutingFields(body);

        await _messageBus.BroadcastAsync(new GitHubWebhookMessage
        {
            Headers = BuildHeaders(headers),
            InstallationId = installationId,
            RepositoryFullName = repositoryFullName,
            EventType = Header(headers, "X-GitHub-Event") ?? string.Empty,
            EventJson = body,
        }, cancellationToken);
    }

    /// <summary>
    /// Pulls <c>installation.id</c> and <c>repository.full_name</c> out of the raw payload without
    /// materialising the event. Every GitHub payload that has them puts them in these two places;
    /// anything malformed yields the same empty values a missing property would.
    /// </summary>
    private static (long InstallationId, string RepositoryFullName) ReadRoutingFields(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            long installationId = 0;
            if (root.TryGetProperty("installation", out var installation)
                && installation.ValueKind == JsonValueKind.Object
                && installation.TryGetProperty("id", out var id)
                && id.TryGetInt64(out var parsed))
            {
                installationId = parsed;
            }

            var fullName = string.Empty;
            if (root.TryGetProperty("repository", out var repository)
                && repository.ValueKind == JsonValueKind.Object
                && repository.TryGetProperty("full_name", out var name)
                && name.ValueKind == JsonValueKind.String)
            {
                fullName = name.GetString() ?? string.Empty;
            }

            return (installationId, fullName);
        }
        catch (JsonException)
        {
            // The signature already proved GitHub sent this, so a body we cannot parse is a shape
            // we do not know rather than an attack. Route it with empty fields instead of losing it.
            return (0, string.Empty);
        }
    }

    private static WebhookHeaders BuildHeaders(IDictionary<string, StringValues> headers)
        => new()
        {
            Event = Header(headers, "X-GitHub-Event")!,
            Delivery = Header(headers, "X-GitHub-Delivery")!,
            HookId = Header(headers, "X-GitHub-Hook-ID")!,
            HookInstallationTargetId = Header(headers, "X-GitHub-Hook-Installation-Target-ID")!,
            HookInstallationTargetType = Header(headers, "X-GitHub-Hook-Installation-Target-Type")!,
            Signature256 = Header(headers, "X-Hub-Signature-256")!,
            UserAgent = Header(headers, "User-Agent")!,
        };

    private static string? Header(IDictionary<string, StringValues> headers, string name)
        => headers.TryGetValue(name, out var value) ? value.ToString() : null;

    // --- Event overrides: each delegates to the shared generic helper ---
    //
    // These exist only to offer a strongly-typed envelope for the events worth one; the catch-all
    // above already covers every event, including the ones absent from this list. Adding an
    // override here is an ergonomic improvement for consumers, never the thing that makes an event
    // reachable.

    protected override ValueTask ProcessPushWebhookAsync(WebhookHeaders headers, PushEvent pushEvent, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, pushEvent, cancellationToken);

    protected override ValueTask ProcessIssuesWebhookAsync(WebhookHeaders headers, IssuesEvent issuesEvent, IssuesAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, issuesEvent, cancellationToken);

    protected override ValueTask ProcessIssueCommentWebhookAsync(WebhookHeaders headers, IssueCommentEvent issueCommentEvent, IssueCommentAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, issueCommentEvent, cancellationToken);

    protected override ValueTask ProcessPullRequestWebhookAsync(WebhookHeaders headers, PullRequestEvent pullRequestEvent, PullRequestAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, pullRequestEvent, cancellationToken);

    protected override ValueTask ProcessPullRequestReviewWebhookAsync(WebhookHeaders headers, PullRequestReviewEvent pullRequestReviewEvent, PullRequestReviewAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, pullRequestReviewEvent, cancellationToken);

    protected override ValueTask ProcessPullRequestReviewCommentWebhookAsync(WebhookHeaders headers, PullRequestReviewCommentEvent pullRequestReviewCommentEvent, PullRequestReviewCommentAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, pullRequestReviewCommentEvent, cancellationToken);

    protected override ValueTask ProcessCheckRunWebhookAsync(WebhookHeaders headers, CheckRunEvent checkRunEvent, CheckRunAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, checkRunEvent, cancellationToken);

    protected override ValueTask ProcessCheckSuiteWebhookAsync(WebhookHeaders headers, CheckSuiteEvent checkSuiteEvent, CheckSuiteAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, checkSuiteEvent, cancellationToken);

    protected override ValueTask ProcessInstallationWebhookAsync(WebhookHeaders headers, InstallationEvent installationEvent, InstallationAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, installationEvent, cancellationToken);

    protected override ValueTask ProcessRepositoryWebhookAsync(WebhookHeaders headers, RepositoryEvent repositoryEvent, RepositoryAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, repositoryEvent, cancellationToken);

    protected override ValueTask ProcessInstallationRepositoriesWebhookAsync(WebhookHeaders headers, InstallationRepositoriesEvent installationRepositoriesEvent, InstallationRepositoriesAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, installationRepositoriesEvent, cancellationToken);

    protected override ValueTask ProcessInstallationTargetWebhookAsync(WebhookHeaders headers, InstallationTargetEvent installationTargetEvent, InstallationTargetAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, installationTargetEvent, cancellationToken);

    protected override ValueTask ProcessOrganizationWebhookAsync(WebhookHeaders headers, OrganizationEvent organizationEvent, OrganizationAction action, CancellationToken cancellationToken = default)
        => HandleWebhookAsync(headers, organizationEvent, cancellationToken);

    // --- Shared handler ---

    /// <summary>
    /// Broadcasts the typed envelope for an event that has one — and only when something consumes
    /// it. A broadcast to a queue with no worker is not a no-op: it stores a document nothing will
    /// ever drain, one per delivery, forever. Since both envelopes are offered for every event, an
    /// app that subscribes only to the catch-all used to pay for a typed document per delivery too.
    /// </summary>
    private async ValueTask HandleWebhookAsync<TEvent>(WebhookHeaders headers, TEvent evt, CancellationToken cancellationToken = default)
        where TEvent : WebhookEvent
    {
        if (!_recipients.HasRecipient<GitHubWebhookMessage<TEvent>>())
        {
            _logger.LogDebug(
                "No recipient for GitHubWebhookMessage<{EventType}>; the catch-all envelope carries this delivery.",
                typeof(TEvent).Name);
            return;
        }

        // No queue-name override: QueueNames derives the name from the closed generic type, and
        // MessageSubscriptionManager derives it identically from the IRecipient<> registration,
        // so the two agree by construction.
        await _messageBus.BroadcastAsync(new GitHubWebhookMessage<TEvent>
        {
            Headers = headers,
            InstallationId = evt.Installation?.Id ?? 0,
            RepositoryFullName = evt.Repository?.FullName ?? string.Empty,
            EventJson = _rawBody ?? string.Empty,
        }, cancellationToken);
    }
}
