using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using CodeCoverage.LookupReferences;
using CodeCoverage.Recipients;
using CodeCoverage.Services;
using MintPlayer.Assertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Recipients;

/// <summary>
/// Whether a card is <b>added</b> to a board, as opposed to moved on it.
/// </summary>
/// <remarks>
/// The rule is type-aware rather than configurable: issues are added when missing, pull requests
/// never are. The app this was migrated from offered a single per-rule <c>AutoAddToProject</c>
/// covering both, and this replaces it — so these tests are the only place the distinction is
/// written down as behaviour rather than as a comment.
/// <para>
/// Worth pinning because getting it wrong is invisible in exactly the direction that hurts. A PR
/// rule that adds would turn "move the PR to In Review" into "and put every pull request on the
/// board", burying the issues the board exists to track under a day of branch pushes — with no
/// error anywhere, just a board nobody wants to look at any more.
/// </para>
/// </remarks>
public class ProjectAutomationMovementTests : CoverageRavenTest
{
    private const string Owner = "MintPlayer";
    private const string Repo = "MintPlayer/MintPlayer.Spark";
    private const string DoneOption = "98236657";

    private async Task<IDocumentStore> SeededStoreAsync(
        IDocumentStore store, EWebhookEventType eventType)
    {
        new GitHubProjects_Overview().Execute(store);

        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new GitHubProject
        {
            NodeId = "PVT_test",
            OwnerLogin = Owner,
            InstallationId = 1,
            Number = 1,
            Name = "Board",
            AutomationEnabled = true,
            StatusFieldId = "PVTSSF_test",
            Columns = [new ProjectColumn { Id = DoneOption, Name = "Done" }],
            EventMappings =
            [
                new EventColumnMapping
                {
                    Id = eventType.ToString(),
                    EventType = eventType.ToString(),
                    TargetColumnOptionId = DoneOption,
                    Enabled = true,
                },
            ],
        }, GitHubProject.DocumentId("PVT_test"));
        await session.SaveChangesAsync();

        WaitForIndexing(store);
        return store;
    }

    private static ProjectAutomationMessage Message(string eventType, string json)
        => new()
        {
            EventType = eventType,
            EventJson = json,
            RepositoryFullName = Repo,
            InstallationId = 1,
            DeliveryId = "d1",
        };

    [Fact]
    public async Task An_issue_event_adds_the_card_when_it_is_not_on_the_board()
    {
        var store = await SeededStoreAsync(GetDocumentStore(), EWebhookEventType.IssuesOpened);
        var cards = Substitute.For<IGitHubProjectCards>();
        cards.MoveIssueAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ECardOutcome.Added);

        using var session = store.OpenAsyncSession();
        var recipient = new ProjectAutomationRecipient(
            session, cards, NullLogger<ProjectAutomationRecipient>.Instance);

        await recipient.HandleAsync(
            Message("issues", """{"action":"opened","issue":{"number":7}}"""));

        await cards.Received(1).MoveIssueAsync(
            Arg.Any<GitHubProject>(), Owner, Arg.Any<string>(), 7, DoneOption,
            addIfMissing: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_pull_request_event_never_adds_the_card()
    {
        var store = await SeededStoreAsync(GetDocumentStore(), EWebhookEventType.PullRequestOpened);
        var cards = Substitute.For<IGitHubProjectCards>();
        cards.MovePullRequestAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ECardOutcome.NotOnBoard);

        using var session = store.OpenAsyncSession();
        var recipient = new ProjectAutomationRecipient(
            session, cards, NullLogger<ProjectAutomationRecipient>.Instance);

        await recipient.HandleAsync(
            Message("pull_request", """{"action":"opened","pull_request":{"number":9}}"""));

        await cards.Received(1).MovePullRequestAsync(
            Arg.Any<GitHubProject>(), Owner, Arg.Any<string>(), 9, DoneOption,
            addIfMissing: false, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A merged PR moves its closing issues, and does <b>not</b> add them — the one issue case that
    /// stays "move only", because the issue is being touched for referencing a PR rather than on
    /// its own account.
    /// </summary>
    [Fact]
    public async Task A_merged_pull_request_moves_its_closing_issues_without_adding_them()
    {
        var store = await SeededStoreAsync(GetDocumentStore(), EWebhookEventType.PullRequestMerged);
        var cards = Substitute.For<IGitHubProjectCards>();
        cards.MovePullRequestAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ECardOutcome.Moved);
        cards.GetClosingIssuesAsync(default, default!, default!, default, default)
            .ReturnsForAnyArgs<IReadOnlyList<(string Repo, int Number)>>([("MintPlayer.Spark", 42)]);

        using var session = store.OpenAsyncSession();
        var recipient = new ProjectAutomationRecipient(
            session, cards, NullLogger<ProjectAutomationRecipient>.Instance);

        await recipient.HandleAsync(
            Message("pull_request", """{"action":"closed","pull_request":{"number":9,"merged":true}}"""));

        await cards.Received(1).MoveIssueAsync(
            Arg.Any<GitHubProject>(), Owner, Arg.Any<string>(), 42, DoneOption,
            addIfMissing: false, Arg.Any<CancellationToken>());
    }
}
