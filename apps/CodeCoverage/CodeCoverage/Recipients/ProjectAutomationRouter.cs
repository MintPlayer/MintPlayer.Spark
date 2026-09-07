using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Recipients;

/// <summary>
/// Decides whether a webhook delivery is worth handing to board automation, and re-publishes it
/// onto that queue if so. A <b>sibling</b> of <see cref="GitHubEventsRecipient"/> on the catch-all
/// queue, never an extension of its switch, so the two have independent retry and dead-letter
/// state.
/// <para>
/// The filtering happens here rather than in the automation recipient for one reason: <b>FR7</b>.
/// Most deliveries concern repositories no board automates, and the cheapest possible answer to
/// "does any board care about this?" is a single indexed RavenDB query. Doing it before the
/// re-publish means an uninteresting delivery costs one query and no document, instead of a
/// document, a lane slot and a claim.
/// </para>
/// </summary>
public partial class ProjectAutomationRouter : IRecipient<GitHubWebhookMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly ILogger<ProjectAutomationRouter> logger;
    [Inject] private readonly Microsoft.Extensions.Options.IOptions<MintPlayer.Spark.Webhooks.GitHub.Configuration.GitHubWebhooksOptions> webhookOptions;

    /// <summary>
    /// The events board automation can act on. Everything else is dropped here, before a query.
    /// Every one of these is a <em>repository</em> event, which is why automation works on a
    /// user-account installation: the Projects V2 events that do not reach user installations
    /// (<c>projects_v2</c>, <c>projects_v2_item</c>) are not in this set and are not needed.
    /// </summary>
    private static readonly HashSet<string> AutomatableEvents = new(StringComparer.Ordinal)
    {
        "issues",
        "pull_request",
        "pull_request_review",
        "issue_comment",
        "check_run",
    };

    public async Task HandleAsync(GitHubWebhookMessage message, CancellationToken cancellationToken = default)
    {
        // Debug, not Information: this fires for every delivery, including the majority that
        // concern repositories no board automates. It is the line that answered "which events do we
        // actually receive" during the live verification recorded in the PRD's §3b, and it is worth
        // keeping for the next time that question comes up — an ungranted permission and "GitHub
        // does not send this event" are indistinguishable from inside the application.
        logger.LogDebug(
            "GitHub delivery: event={EventType} repo={Repo} installation={InstallationId} delivery={Delivery}",
            message.EventType,
            string.IsNullOrEmpty(message.RepositoryFullName) ? "<none>" : message.RepositoryFullName,
            message.InstallationId,
            message.Headers.Delivery);

        if (!AutomatableEvents.Contains(message.EventType))
            return;

        if (string.IsNullOrEmpty(message.RepositoryFullName))
            return;

        // FR6 / C10 — drop deliveries this app caused itself, BEFORE anything else looks at them.
        // CodeCoverage creates check runs and posts pull-request comments, so it receives webhooks
        // for its own writes. Unguarded, publishing coverage feedback moves a card, and a
        // card move that posts feedback moves it again.
        if (IsSelfAuthored(message))
        {
            logger.LogDebug(
                "Dropping self-authored {EventType} delivery {DeliveryId}; it was caused by this app's own write",
                message.EventType, message.Headers.Delivery);
            return;
        }

        var owner = message.RepositoryFullName.Split('/')[0];

        // FR7 — one indexed query, and return before any GitHub call if nothing automates this
        // owner. This replaces an unpaged "load every board" per delivery.
        //
        // Filtered on both AutomationEnabled and reachability: a disconnected board must not act,
        // because we may no longer have a token that can touch it. `!= Disconnected` rather than
        // `== Connected` so documents written before the field existed still match.
        var automates = await session.Query<GitHubProject, GitHubProjects_Overview>()
            .Where(p => p.OwnerLogin == owner
                        && p.AutomationEnabled
                        && p.Connection != RepositoryConnection.Disconnected)
            .AnyAsync(cancellationToken);

        if (!automates)
            return;

        // Re-published rather than handled here, so board work runs on its own FIFO lane instead of
        // sharing one with account synchronization. Keyed on the delivery id so a GitHub redelivery
        // does not enqueue a second automation message — the catch-all's own de-duplication does
        // not cover a message this handler creates.
        await messageBus.BroadcastOnceAsync(
            new ProjectAutomationMessage
            {
                EventType = message.EventType,
                InstallationId = message.InstallationId,
                RepositoryFullName = message.RepositoryFullName,
                EventJson = message.EventJson,
                DeliveryId = message.Headers.Delivery,
            },
            $"automation-{message.Headers.Delivery}",
            cancellationToken);
    }

    /// <summary>
    /// Whether this delivery was caused by one of <b>this</b> app's own writes.
    /// <para>
    /// The test is the App <em>id</em>, compared against our configured one, and the precision
    /// matters in both directions. Too narrow and publishing coverage feedback moves a card, which
    /// publishes more feedback. Too broad and a mapping becomes permanently inert — which is the
    /// trap here, because <b>every</b> check run on GitHub is created by an App or by Actions, so a
    /// guard that dropped "any App's check run" would drop every <c>check_run</c> delivery and the
    /// <c>CheckRunCompleted</c> rule would never fire once. That is exactly the class of defect this
    /// migration exists to fix, and it would have been invisible: the rule would look configured
    /// and simply do nothing.
    /// </para>
    /// <para>
    /// <c>check_run.app.id</c> carries the creating App. For everything else GitHub supplies
    /// <c>performed_via_github_app</c>, its own "an App did this" marker, which is present on
    /// <c>issue_comment</c> and absent for human actions.
    /// </para>
    /// <para>
    /// If <c>AppId</c> is not configured the guard <b>fails closed</b> and drops nothing, because
    /// silently disabling the loop guard is worse than a noisy loop: a loop is visible in seconds,
    /// while a guard that quietly stopped comparing is not.
    /// </para>
    /// </summary>
    private bool IsSelfAuthored(GitHubWebhookMessage message)
    {
        // ProductionAppId, NOT DevelopmentAppId, and the naming is a trap worth naming. Despite the
        // name, ProductionAppId means "the App whose webhooks THIS instance processes" — locally
        // that is the dev App. DevelopmentAppId means something else entirely: "forward that App's
        // webhooks to connected dev clients instead of processing them". So our own identity, in
        // every environment, is ProductionAppId; comparing against DevelopmentAppId would match
        // nothing locally and the wrong App in production.
        if (webhookOptions.Value.ProductionAppId is not { } appId)
        {
            logger.LogWarning(
                "GitHub:{{Env}}:AppId is not configured, so self-authored deliveries cannot be identified; "
                + "board automation may react to this app's own check runs and comments");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(message.EventJson);
            var root = document.RootElement;

            return message.EventType switch
            {
                "check_run" => IsOurCheckRun(root, appId),
                _ => IsPerformedByUs(root, appId),
            };
        }
        catch (JsonException)
        {
            // The signature already proved GitHub sent this, so an unparseable body is a shape we
            // do not know rather than an attack. Treat it as not-self-authored and let the
            // automation recipient decide: it will fail to deserialize and dead-letter with a
            // reason, which is more useful than silently dropping the delivery here.
            return false;
        }
    }

    private static bool IsOurCheckRun(JsonElement root, long appId)
        => root.TryGetProperty("check_run", out var checkRun)
            && checkRun.ValueKind == JsonValueKind.Object
            && checkRun.TryGetProperty("app", out var app)
            && app.ValueKind == JsonValueKind.Object
            && app.TryGetProperty("id", out var id)
            && id.TryGetInt64(out var actual)
            && actual == appId;

    private static bool IsPerformedByUs(JsonElement root, long appId)
        => root.TryGetProperty("performed_via_github_app", out var app)
            && app.ValueKind == JsonValueKind.Object
            && app.TryGetProperty("id", out var id)
            && id.TryGetInt64(out var actual)
            && actual == appId;
}
