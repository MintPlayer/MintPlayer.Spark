using CodeCoverage.LookupReferences;
using CodeCoverage.Recipients;
using MintPlayer.Assertions;
using MintPlayer.Spark.Webhooks.GitHub.Configuration;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Octokit.Webhooks;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Recipients;

/// <summary>
/// The two board-automation decisions that fail <b>silently</b> when wrong.
/// </summary>
/// <remarks>
/// Neither of these produces an error, a log line or a failed request when it misbehaves — a rule
/// simply never fires, or fires twice. That is the whole reason they are worth pinning, and the
/// reason both are tested directly rather than through a webhook round trip: a test that drove the
/// full path would also pass with either defect present, because both defects look exactly like
/// "no board matched".
/// </remarks>
public class ProjectAutomationTests
{
    private static ProjectAutomationRouter Router(long? appId)
    {
        var options = Options.Create(new GitHubWebhooksOptions { ProductionAppId = appId });
        return new ProjectAutomationRouter(
            Substitute.For<IAsyncDocumentSession>(),
            Substitute.For<MintPlayer.Spark.Messaging.Abstractions.IMessageBus>(),
            NullLogger<ProjectAutomationRouter>.Instance,
            options);
    }

    private static GitHubWebhookMessage Delivery(string eventType, string json)
        => new()
        {
            EventType = eventType,
            EventJson = json,
            RepositoryFullName = "MintPlayer/MintPlayer.Spark",
            InstallationId = 1,
            Headers = new WebhookHeaders { Event = eventType, Delivery = "d1" },
        };

    // ---------------------------------------------------------------- the loop guard

    /// <summary>
    /// The trap this guard was written around: <b>every</b> check run on GitHub is created by an App
    /// or by Actions, so a guard that dropped "any App's check run" would drop every
    /// <c>check_run</c> delivery and <c>CheckRunCompleted</c> would never fire once.
    /// </summary>
    [Fact]
    public void A_check_run_from_another_app_is_not_self_authored()
    {
        var router = Router(appId: 4567511);
        var message = Delivery("check_run", """{"check_run":{"app":{"id":99999}}}""");

        router.IsSelfAuthored(message).Should().BeFalse(
            "a check run from a different App is somebody else's, and dropping it would make every "
            + "CheckRunCompleted rule permanently inert");
    }

    [Fact]
    public void A_check_run_from_this_app_is_self_authored()
    {
        var router = Router(appId: 4567511);
        var message = Delivery("check_run", """{"check_run":{"app":{"id":4567511}}}""");

        router.IsSelfAuthored(message).Should().BeTrue(
            "publishing coverage feedback must not move a card that publishes more feedback");
    }

    /// <summary>
    /// GitHub hangs <c>performed_via_github_app</c> off the <b>resource</b>, never off the payload
    /// root. This test is what found the guard reading the root only, which made it inert for every
    /// event except <c>check_run</c>.
    /// </summary>
    [Fact]
    public void A_comment_this_app_posted_is_self_authored()
    {
        var router = Router(appId: 4567511);
        var message = Delivery("issue_comment", """{"action":"created","comment":{"performed_via_github_app":{"id":4567511}}}""");

        router.IsSelfAuthored(message).Should().BeTrue(
            "the marker sits on the comment, not at the top level");
    }

    [Fact]
    public void A_comment_another_app_posted_is_not_self_authored()
    {
        var router = Router(appId: 4567511);
        var message = Delivery("issue_comment", """{"action":"created","comment":{"performed_via_github_app":{"id":99999}}}""");

        router.IsSelfAuthored(message).Should().BeFalse();
    }

    [Fact]
    public void A_comment_a_human_posted_is_not_self_authored()
    {
        var router = Router(appId: 4567511);
        // No performed_via_github_app at all -- GitHub omits it for human actions.
        var message = Delivery("issue_comment", """{"comment":{"body":"looks good"}}""");

        router.IsSelfAuthored(message).Should().BeFalse();
    }

    /// <summary>
    /// Unconfigured means "drop nothing", not "drop everything". A visible loop is diagnosable in
    /// seconds; a guard that quietly stopped comparing is not.
    /// </summary>
    [Fact]
    public void With_no_app_id_configured_the_guard_drops_nothing()
    {
        var router = Router(appId: null);
        var message = Delivery("check_run", """{"check_run":{"app":{"id":4567511}}}""");

        router.IsSelfAuthored(message).Should().BeFalse();
    }

    [Fact]
    public void A_malformed_payload_is_not_treated_as_self_authored()
    {
        var router = Router(appId: 4567511);
        var message = Delivery("check_run", "not json at all");

        router.IsSelfAuthored(message).Should().BeFalse(
            "an unparseable payload must not silently disable automation for that delivery");
    }

    // ------------------------------------------------- event-key disambiguation

    private static ProjectAutomationMessage Automation(string eventType, string json)
        => new()
        {
            EventType = eventType,
            EventJson = json,
            RepositoryFullName = "MintPlayer/MintPlayer.Spark",
            InstallationId = 1,
            DeliveryId = "d1",
        };

    /// <summary>
    /// GitHub reports a merge as <c>pull_request.closed</c> and distinguishes it only by the
    /// payload's <c>merged</c> flag, so a resolver keyed on (event, action) alone maps a merge onto
    /// the "closed" rule and drops the "merged" one.
    /// </summary>
    [Fact]
    public void A_merged_pull_request_resolves_to_merged_and_not_to_closed()
    {
        var keys = ProjectAutomationRecipient.ResolveEventKeys(
            Automation("pull_request", """{"action":"closed","pull_request":{"merged":true}}"""));

        keys.Should().Contain(EWebhookEventType.PullRequestMerged);
        keys.Should().NotContain(EWebhookEventType.PullRequestClosed);
    }

    [Fact]
    public void A_pull_request_closed_without_merging_resolves_to_closed_only()
    {
        var keys = ProjectAutomationRecipient.ResolveEventKeys(
            Automation("pull_request", """{"action":"closed","pull_request":{"merged":false}}"""));

        keys.Should().Contain(EWebhookEventType.PullRequestClosed);
        keys.Should().NotContain(EWebhookEventType.PullRequestMerged);
    }

    /// <summary>
    /// The second colliding pair: both arrive as <c>pull_request_review.submitted</c>, separated
    /// only by the review's state.
    /// </summary>
    [Fact]
    public void An_approving_review_resolves_to_approved_only()
    {
        var keys = ProjectAutomationRecipient.ResolveEventKeys(
            Automation("pull_request_review", """{"action":"submitted","review":{"state":"approved"}}"""));

        keys.Should().Contain(EWebhookEventType.PullRequestReviewApproved);
        keys.Should().NotContain(EWebhookEventType.PullRequestReviewChangesRequested);
    }

    [Fact]
    public void A_changes_requested_review_resolves_to_changes_requested_only()
    {
        var keys = ProjectAutomationRecipient.ResolveEventKeys(
            Automation("pull_request_review", """{"action":"submitted","review":{"state":"changes_requested"}}"""));

        keys.Should().Contain(EWebhookEventType.PullRequestReviewChangesRequested);
        keys.Should().NotContain(EWebhookEventType.PullRequestReviewApproved);
    }

    /// <summary>
    /// A payload with no <c>action</c> resolves to nothing rather than to everything. Worth pinning
    /// because the opposite default would fire every rule on the board for one malformed delivery.
    /// </summary>
    [Fact]
    public void A_payload_with_no_action_resolves_to_nothing()
    {
        ProjectAutomationRecipient.ResolveEventKeys(
            Automation("pull_request", """{"pull_request":{"merged":true}}""")).Should().BeEmpty();
    }

    [Fact]
    public void An_ordinary_event_resolves_to_its_single_key()
    {
        var keys = ProjectAutomationRecipient.ResolveEventKeys(
            Automation("issues", """{"action":"opened"}"""));

        keys.Should().BeEquivalentTo([EWebhookEventType.IssuesOpened]);
    }
}
