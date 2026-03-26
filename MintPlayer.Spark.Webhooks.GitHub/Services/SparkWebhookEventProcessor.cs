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
using Octokit.Webhooks.Events.IssueComment;
using Octokit.Webhooks.Events.Issues;
using Octokit.Webhooks.Events.PullRequest;
using Octokit.Webhooks.Events.PullRequestReview;
using Octokit.Webhooks.Events.PullRequestReviewComment;
using Octokit.Webhooks.Events.Repository;

namespace MintPlayer.Spark.Webhooks.GitHub.Services;

[Register(typeof(WebhookEventProcessor), ServiceLifetime.Scoped)]
internal partial class SparkWebhookEventProcessor : WebhookEventProcessor
{
    [Inject] private readonly IMessageBus _messageBus;
    [Options] private readonly IOptions<GitHubWebhooksOptions> _options;
    [Inject] private readonly ISignatureService _signatureService;
    [Inject] private readonly IServiceProvider _serviceProvider;
    [Inject] private readonly ILogger<SparkWebhookEventProcessor> _logger;

    // Stashed per-request for catch-all message and dev forwarding
    private string? _rawBody;
    private IDictionary<string, StringValues>? _rawHeaders;

    public override async ValueTask ProcessWebhookAsync(IDictionary<string, StringValues> headers, string body, CancellationToken cancellationToken = default)
    {
        // Octokit uses case-sensitive dictionary — make it case-insensitive
        var caseInsensitiveHeaders = new Dictionary<string, StringValues>(headers, StringComparer.OrdinalIgnoreCase);

        // Validate webhook signature
        if (!string.IsNullOrEmpty(_options.Value.WebhookSecret))
        {
            caseInsensitiveHeaders.TryGetValue("X-Hub-Signature-256", out var signatureSha256);
            if (!_signatureService.VerifySignature(signatureSha256, _options.Value.WebhookSecret, body))
            {
                _logger.LogWarning("GitHub webhook signature validation failed — dropping event");
                return;
            }
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
                    await devSocketService.SendToClients(caseInsensitiveHeaders, body);
                }
                return;
            }
        }

        // Stash for use in specific event handlers
        _rawHeaders = caseInsensitiveHeaders;
        _rawBody = body;

        await base.ProcessWebhookAsync(caseInsensitiveHeaders, body, cancellationToken);
    }

    // --- Event overrides: each delegates to the shared generic helper ---

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

    // --- Shared handler ---

    private async ValueTask HandleWebhookAsync<TEvent>(WebhookHeaders headers, TEvent evt, CancellationToken cancellationToken = default)
        where TEvent : WebhookEvent
    {
        var installationId = evt.Installation?.Id ?? 0;
        var repoFullName = evt.Repository?.FullName ?? string.Empty;

        // Broadcast event-specific typed message
        // No queue name override — MessageBus uses typeof(GitHubWebhookMessage<TEvent>).FullName,
        // which matches what MessageSubscriptionManager discovers from IRecipient<> registrations.
        var typedMessage = new GitHubWebhookMessage<TEvent>
        {
            Headers = headers,
            InstallationId = installationId,
            RepositoryFullName = repoFullName,
            EventJson = _rawBody ?? string.Empty,
        };
        await _messageBus.BroadcastAsync(typedMessage, cancellationToken);

        // Broadcast catch-all message
        var catchAllMessage = new GitHubWebhookMessage
        {
            Headers = headers,
            InstallationId = installationId,
            RepositoryFullName = repoFullName,
            EventType = headers.Event ?? string.Empty,
            EventJson = _rawBody ?? string.Empty,
        };
        await _messageBus.BroadcastAsync(catchAllMessage, cancellationToken);
    }
}
