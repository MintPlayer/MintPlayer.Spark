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
/// One rule-level flag decides it — <c>AutoAddToBoard</c>, default <see langword="true"/> — and it
/// decides it for <b>every</b> item the rule touches: the event's own subject, and each linked
/// issue when <c>MoveLinkedIssues</c> is on. These tests are the only place that is written down as
/// behaviour rather than as a comment, and the only thing standing between the flag and a return of
/// the policy it replaced.
/// <para>
/// Because it was type-aware and hard-coded first: issues always added, pull requests never, and a
/// linked-issue-only <c>AddLinkedIfMissing</c> alongside. That is wrong in a way nothing reports —
/// combined with linked-issue movement it left every non-merged PR rule with no item to act on, so
/// the rule completed "successfully" having done nothing and even <c>LastError</c> stayed null.
/// </para>
/// <para>
/// The risk that policy was reaching for is real and is now the user's to take: a board that tracks
/// issues and turns this on will collect a card per pull request. That is what the flag is for. So
/// the facts worth pinning are both directions — on adds, off adds <em>nothing at all</em>, and
/// neither is inferred from whether the item is an issue or a PR.
/// </para>
/// </remarks>
public class ProjectAutomationMovementTests : CoverageRavenTest
{
    private const string Owner = "MintPlayer";
    private const string Repo = "MintPlayer/MintPlayer.Spark";
    private const string DoneOption = "98236657";

    private async Task<IDocumentStore> SeededStoreAsync(
        IDocumentStore store, EWebhookEventType eventType,
        bool moveLinkedIssues = true, bool autoAddToBoard = true)
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
                    MoveLinkedIssues = moveLinkedIssues,
                    AutoAddToBoard = autoAddToBoard,
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

    /// <summary>
    /// A pull request IS added when the rule says so, and this is the case the previous
    /// implementation could not express at all: the PR path was hard-coded to never add, so a board
    /// that tracks pull requests had no way to get a card onto it.
    /// </summary>
    [Fact]
    public async Task A_pull_request_event_adds_the_card_when_the_rule_says_so()
    {
        var store = await SeededStoreAsync(GetDocumentStore(), EWebhookEventType.PullRequestOpened);
        var cards = Substitute.For<IGitHubProjectCards>();
        cards.MovePullRequestAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ECardOutcome.Added);

        using var session = store.OpenAsyncSession();
        var recipient = new ProjectAutomationRecipient(
            session, cards, NullLogger<ProjectAutomationRecipient>.Instance);

        await recipient.HandleAsync(
            Message("pull_request", """{"action":"opened","pull_request":{"number":9}}"""));

        await cards.Received(1).MovePullRequestAsync(
            Arg.Any<GitHubProject>(), Owner, Arg.Any<string>(), 9, DoneOption,
            addIfMissing: true, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// With the flag off, the rule adds <b>nothing</b> — not the pull request, and not the issue it
    /// closes. One flag, both items; the item's type does not enter into it.
    /// </summary>
    /// <remarks>
    /// This is the board-flooding case the old hard-coded policy existed to prevent, and it still
    /// works — it is now a setting rather than a rule nobody could see or change.
    /// </remarks>
    [Fact]
    public async Task A_rule_with_auto_add_off_adds_neither_the_pull_request_nor_its_linked_issue()
    {
        var store = await SeededStoreAsync(
            GetDocumentStore(), EWebhookEventType.PullRequestOpened, autoAddToBoard: false);
        var cards = Substitute.For<IGitHubProjectCards>();
        cards.MovePullRequestAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ECardOutcome.NotOnBoard);
        cards.GetClosingIssuesAsync(default, default!, default!, default, default)
            .ReturnsForAnyArgs<IReadOnlyList<(string Repo, int Number)>>([("MintPlayer.Spark", 42)]);

        using var session = store.OpenAsyncSession();
        var recipient = new ProjectAutomationRecipient(
            session, cards, NullLogger<ProjectAutomationRecipient>.Instance);

        await recipient.HandleAsync(
            Message("pull_request", """{"action":"opened","pull_request":{"number":9}}"""));

        await cards.Received(1).MovePullRequestAsync(
            Arg.Any<GitHubProject>(), Owner, Arg.Any<string>(), 9, DoneOption,
            addIfMissing: false, Arg.Any<CancellationToken>());
        await cards.Received(1).MoveIssueAsync(
            Arg.Any<GitHubProject>(), Owner, Arg.Any<string>(), 42, DoneOption,
            addIfMissing: false, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An issue event with the flag off moves a card that is already there and recruits nothing —
    /// the mirror of the issue path's old hard-coded <c>true</c>, which no rule could turn off.
    /// </summary>
    [Fact]
    public async Task An_issue_event_with_auto_add_off_does_not_add_the_card()
    {
        var store = await SeededStoreAsync(
            GetDocumentStore(), EWebhookEventType.IssuesOpened, autoAddToBoard: false);
        var cards = Substitute.For<IGitHubProjectCards>();
        cards.MoveIssueAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ECardOutcome.NotOnBoard);

        using var session = store.OpenAsyncSession();
        var recipient = new ProjectAutomationRecipient(
            session, cards, NullLogger<ProjectAutomationRecipient>.Instance);

        await recipient.HandleAsync(
            Message("issues", """{"action":"opened","issue":{"number":7}}"""));

        await cards.Received(1).MoveIssueAsync(
            Arg.Any<GitHubProject>(), Owner, Arg.Any<string>(), 7, DoneOption,
            addIfMissing: false, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A merged PR moves its closing issues, and with the flag off does <b>not</b> add them.
    /// </summary>
    /// <remarks>
    /// The "merged → Done" shape is the one where turning the flag off is most defensible: work
    /// nobody put on the board arguably should not appear on it just because it finished. Pinned
    /// with the flag explicit rather than by default, because the default is now <c>true</c>.
    /// </remarks>
    [Fact]
    public async Task A_merged_pull_request_moves_its_closing_issues_without_adding_them()
    {
        var store = await SeededStoreAsync(
            GetDocumentStore(), EWebhookEventType.PullRequestMerged, autoAddToBoard: false);
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

    /// <summary>
    /// A <c>check_run</c> reaches a card only through the pull requests its head commit belongs to,
    /// and that path reads the same flag as every other — it was the third hard-coded
    /// <c>addIfMissing: false</c>.
    /// </summary>
    [Fact]
    public async Task A_check_run_event_adds_the_pull_request_when_the_rule_says_so()
    {
        var store = await SeededStoreAsync(GetDocumentStore(), EWebhookEventType.CheckRunCompleted);
        var cards = Substitute.For<IGitHubProjectCards>();
        cards.MovePullRequestAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ECardOutcome.Added);

        using var session = store.OpenAsyncSession();
        var recipient = new ProjectAutomationRecipient(
            session, cards, NullLogger<ProjectAutomationRecipient>.Instance);

        await recipient.HandleAsync(Message(
            "check_run",
            """{"action":"completed","check_run":{"pull_requests":[{"number":11}]}}"""));

        await cards.Received(1).MovePullRequestAsync(
            Arg.Any<GitHubProject>(), Owner, Arg.Any<string>(), 11, DoneOption,
            addIfMissing: true, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The reported bug: marking a PR ready for review moved nothing, because linked-issue movement
    /// was hard-coded to <c>PullRequestMerged</c> alone.
    /// </summary>
    /// <remarks>
    /// This is the case that matters most in practice and the one that was broken. On a board that
    /// tracks issues the PR's own card usually does not exist, so the linked issue is the entire
    /// outcome of the rule — and with the PR path hard-coded never to add, the rule completed
    /// "successfully" having done nothing, with <c>LastError</c> null.
    /// </remarks>
    [Fact]
    public async Task A_ready_for_review_pull_request_moves_its_linked_issues()
    {
        var store = await SeededStoreAsync(GetDocumentStore(), EWebhookEventType.PullRequestReadyForReview);
        var cards = Substitute.For<IGitHubProjectCards>();
        cards.MovePullRequestAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ECardOutcome.NotOnBoard);
        cards.GetClosingIssuesAsync(default, default!, default!, default, default)
            .ReturnsForAnyArgs<IReadOnlyList<(string Repo, int Number)>>([("MintPlayer.Spark", 376)]);

        using var session = store.OpenAsyncSession();
        var recipient = new ProjectAutomationRecipient(
            session, cards, NullLogger<ProjectAutomationRecipient>.Instance);

        await recipient.HandleAsync(
            Message("pull_request", """{"action":"ready_for_review","pull_request":{"number":377}}"""));

        await cards.Received(1).MoveIssueAsync(
            Arg.Any<GitHubProject>(), Owner, Arg.Any<string>(), 376, DoneOption,
            addIfMissing: true, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// <c>MoveLinkedIssues = false</c> must not even ask GitHub for the closing issues — the lookup
    /// is a GraphQL round trip, so skipping the move without skipping the query would pay for it on
    /// every delivery and rate-limit the installation for nothing.
    /// </summary>
    [Fact]
    public async Task A_rule_with_linked_issue_movement_off_never_looks_them_up()
    {
        var store = await SeededStoreAsync(
            GetDocumentStore(), EWebhookEventType.PullRequestReadyForReview, moveLinkedIssues: false);
        var cards = Substitute.For<IGitHubProjectCards>();
        cards.MovePullRequestAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ECardOutcome.Moved);

        using var session = store.OpenAsyncSession();
        var recipient = new ProjectAutomationRecipient(
            session, cards, NullLogger<ProjectAutomationRecipient>.Instance);

        await recipient.HandleAsync(
            Message("pull_request", """{"action":"ready_for_review","pull_request":{"number":377}}"""));

        await cards.DidNotReceiveWithAnyArgs().GetClosingIssuesAsync(
            default, default!, default!, default, default);
        await cards.DidNotReceiveWithAnyArgs().MoveIssueAsync(
            default!, default!, default!, default, default!, default, default);
    }

    /// <summary>
    /// <c>AutoAddToBoard</c> reaches the card service for a <b>linked</b> issue too, so a team that
    /// files the issue after opening the PR gets it recruited — the same flag, on the same rule,
    /// governing an item the event was not directly about.
    /// </summary>
    [Fact]
    public async Task A_rule_that_opts_in_adds_a_linked_issue_that_is_not_on_the_board()
    {
        var store = await SeededStoreAsync(
            GetDocumentStore(), EWebhookEventType.PullRequestReadyForReview, autoAddToBoard: true);
        var cards = Substitute.For<IGitHubProjectCards>();
        cards.MovePullRequestAsync(default!, default!, default!, default, default, default, default)
            .ReturnsForAnyArgs(ECardOutcome.NotOnBoard);
        cards.GetClosingIssuesAsync(default, default!, default!, default, default)
            .ReturnsForAnyArgs<IReadOnlyList<(string Repo, int Number)>>([("MintPlayer.Spark", 376)]);

        using var session = store.OpenAsyncSession();
        var recipient = new ProjectAutomationRecipient(
            session, cards, NullLogger<ProjectAutomationRecipient>.Instance);

        await recipient.HandleAsync(
            Message("pull_request", """{"action":"ready_for_review","pull_request":{"number":377}}"""));

        await cards.Received(1).MoveIssueAsync(
            Arg.Any<GitHubProject>(), Owner, Arg.Any<string>(), 376, DoneOption,
            addIfMissing: true, Arg.Any<CancellationToken>());
    }
}
