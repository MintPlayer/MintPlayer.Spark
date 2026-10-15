using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using Xunit;
using static CodeCoverage.Tests.MergeQueue.MergeQueueHarness;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// The "Add to merge queue" button on the queue's own check (merge-queue PRD §0 O30, D88): offered on a pull request the
/// queue could take and does not hold, kept when one leaves the queue without merging, and a click is a command that
/// goes through every check a label or <c>/queue</c> goes through. The click's webhook is <c>MergeQueueWebhookTests</c>'s.
/// </summary>
public class MergeQueueCheckButtonTests : CoverageRavenTest
{
    private const string Panel = "https://coverage.example/github/r/acme/widget";
    private const string Guide = "https://coverage.example/merge-queue/setup";
    private const string Pushed = "9999999999999999999999999999999999999999";

    private async Task<MergeQueueHarness> HarnessAsync()
    {
        var harness = new MergeQueueHarness(GetDocumentStore());
        await harness.SeedAsync();
        return harness;
    }

    private static Task OfferAsync(MergeQueueHarness h, string sha = Head) => h.SignalAsync(EMergeQueueSignal.Offer, Number, sha);

    private static void ShouldOfferTheButton(ForgeQueueCheck check)
    {
        check.State.Should().Be(EForgeQueueCheckState.Neutral);
        check.Actions.Should().Equal([ForgeQueueCheckAction.Enqueue]);
        check.PullRequestNumber.Should().Be(Number);
    }

    private static GitWriterPrepareResult ConflictOutsideTheAllowList() => new(GitWriterOutcomes.Evicted, "conflict-outside-allow-list", BaseSha, Head, null, [],
        ["apps/A/App_Data/translations.json"], "translations.json conflicts", 10);

    // ── The offer ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_open_ready_pull_request_into_a_queue_branch_is_offered_the_button_on_its_current_head()
    {
        var h = await HarnessAsync();

        // The event named an older head: a push overtook it before the saga got to it.
        await OfferAsync(h, sha: OldBase);

        var (sha, check, existing) = h.Queue.QueueChecks.Should().ContainSingle().Which;
        sha.Should().Be(Head, "the head the forge reports now, not the event's");
        existing.HasValue.Should().BeFalse();
        ShouldOfferTheButton(check);
        check.Title.Should().Be("Not queued");
        check.DetailsUrl.Should().Be(Panel);
        check.Summary.Should().Contain("**Add to merge queue** button").And.Contain("the `merge-queue` label").And.Contain("a `/queue` comment")
            .And.Contain($"]({Panel})").And.Contain($"]({Guide})");
        (await h.EntryAsync()).Should().BeNull("an offer is not an entry");
        (await h.QueueDocumentAsync()).Should().BeNull("nor does it create the repository's queue");
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("closed")]
    [InlineData("develop")]
    public async Task No_button_for_a_draft_a_closed_pull_request_or_a_base_the_queue_does_not_serve(string what)
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = what switch
        {
            "draft" => PullRequest() with { IsDraft = true },
            "closed" => PullRequest() with { IsOpen = false },
            _ => PullRequest() with { BaseRef = what },
        };

        await OfferAsync(h);

        h.Queue.QueueChecks.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mergeQueue:\n  enabled: false\n  branches: [master]\n")]
    [InlineData("mergeQueue:\n  enabled: true\n  autoResolve:\n    - path: \"**/App_Data/modelHashes.json\"\n      strategy: take-base\n")]
    public async Task No_button_in_a_repository_whose_default_branch_does_not_turn_a_working_queue_on(string? yml)
    {
        var h = await HarnessAsync();
        if (yml is null) h.Forge.Files.Remove("master/coverage.yml");
        else h.Forge.Files["master/coverage.yml"] = yml;

        await OfferAsync(h);

        h.Queue.QueueChecks.Should().BeEmpty();
    }

    [Fact]
    public async Task No_button_on_a_forge_whose_checks_carry_none()
    {
        var h = await HarnessAsync();
        h.Forge.Capabilities = [.. h.Forge.Capabilities.Except([EForgeCapability.CheckButtons])];
        h.Queue.Roles["triager"] = EForgeRepositoryRole.Triage;

        await OfferAsync(h);
        h.Queue.QueueChecks.Should().BeEmpty();

        await h.EnqueueAsync("triager");
        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Refused);
        h.Queue.LastCheck.Actions.Should().BeEmpty("a check on such a forge never carries one");
    }

    [Fact]
    public async Task No_offer_while_the_pull_request_is_queued()
    {
        var h = await HarnessAsync();
        await h.EnqueueAsync();
        var published = h.Queue.QueueChecks.Count;

        await OfferAsync(h);

        h.Queue.QueueChecks.Should().HaveCount(published, "the queue's own check speaks for a queued pull request");
    }

    // ── The click ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_click_by_a_writer_queues_it_on_a_new_check_run_that_takes_the_name_over()
    {
        var h = await HarnessAsync();
        await OfferAsync(h);
        var offered = h.Queue.QueueChecks.Count;

        await h.EnqueueAsync("maintainer", EMergeQueueCommandSource.Check);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Preparing);
        entry.EnqueuedBy.Should().Be("maintainer");
        entry.EnqueuedVia.Should().Be(EMergeQueueCommandSource.Check);
        h.Queue.RoleQueries.Should().Contain("maintainer", "the clicker's role, as for a label's sender");
        var queued = h.Queue.QueueChecks.Skip(offered).ToList();
        queued[0].ExistingId.HasValue.Should().BeFalse("a completed check run is never reopened: a new one takes the name over");
        queued.Should().OnlyContain(c => c.Check.Actions.Count == 0, "no button while queued");
        h.Queue.LastCheck.Summary.Should().Contain("| Queued by | @maintainer (check) |");
        h.Queue.Comments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_click_by_someone_without_write_is_refused_with_a_comment_and_the_button_stays()
    {
        var h = await HarnessAsync();
        h.Queue.Roles["reader"] = EForgeRepositoryRole.Read;

        await h.EnqueueAsync("reader", EMergeQueueCommandSource.Check);

        var entry = await h.EntryAsync();
        entry.State.Should().Be(EMergeQueueEntryState.Refused);
        entry.EvictionReason.Should().Be("role");
        h.Queue.Comments.Should().ContainSingle().Which.Body.Should().Contain("@reader needs at least write access");
        h.Queue.RemovedLabels.Should().BeEmpty("a click is answered like a comment: there is no label to take back");
        ShouldOfferTheButton(h.Queue.LastCheck);
        h.Queue.LastCheck.Title.Should().Be("Refused: role");
        h.Queue.LastCheck.Summary.Should().Contain("Queue it again with the **Add to merge queue** button");
        h.Writer.Prepares.Should().BeEmpty();
    }

    [Theory]
    [InlineData("draft", "draft")]
    [InlineData("develop", "branch")]
    public async Task A_refusal_a_click_cannot_change_leaves_no_button(string what, string code)
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Number] = what == "draft" ? PullRequest() with { IsDraft = true } : PullRequest() with { BaseRef = what };

        await h.EnqueueAsync(source: EMergeQueueCommandSource.Comment);

        (await h.EntryAsync()).EvictionReason.Should().Be(code);
        h.Queue.LastCheck.State.Should().Be(EForgeQueueCheckState.Neutral);
        h.Queue.LastCheck.Title.Should().Be($"Refused: {code}");
        h.Queue.LastCheck.Actions.Should().BeEmpty();
    }

    // ── Leaving the queue without merging ────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_eviction_keeps_the_button_and_its_title_says_why()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(ConflictOutsideTheAllowList());

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Evicted);
        var (sha, check, existing) = h.Queue.QueueChecks[^1];
        sha.Should().Be(Head);
        existing.HasValue.Should().BeTrue("the run's own check run ends with the button on it");
        ShouldOfferTheButton(check);
        check.Title.Should().Be("Evicted: conflict-outside-allow-list");
        check.Summary.Should().Contain("Queue it again with the **Add to merge queue** button on this check, the `merge-queue` label or a `/queue` comment.");
    }

    [Fact]
    public async Task A_dequeue_keeps_the_button_but_a_close_does_not()
    {
        var h = await HarnessAsync();
        await h.EnqueueAsync();
        await h.DequeueAsync("maintainer");

        ShouldOfferTheButton(h.Queue.LastCheck);
        h.Queue.LastCheck.Title.Should().Be("Dequeued");

        await h.EnqueueAsync();
        await h.SignalAsync(EMergeQueueSignal.Closed, Number);

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Dequeued);
        h.Queue.LastCheck.State.Should().Be(EForgeQueueCheckState.Neutral);
        h.Queue.LastCheck.Actions.Should().BeEmpty("a closed pull request cannot be queued");
    }

    [Fact]
    public async Task A_push_that_evicts_offers_the_button_on_the_new_head()
    {
        var h = await HarnessAsync();
        await h.EnqueueAsync();
        h.Forge.PullRequests[Number] = PullRequest(head: Pushed);

        await h.SignalAsync(EMergeQueueSignal.HeadPushed, Number, Pushed);

        (await h.EntryAsync()).EvictionReason.Should().Be("author-push");
        h.Queue.QueueChecks.Last(c => c.Sha == Head).Check.Title.Should().Be("Evicted: author-push");
        var offer = h.Queue.QueueChecks.Should().ContainSingle(c => c.Sha == Pushed).Which.Check;
        ShouldOfferTheButton(offer);
        offer.Title.Should().Be("Not queued");
    }

    [Fact]
    public async Task A_push_a_step_finds_offers_the_button_on_the_new_head_too()
    {
        var h = await HarnessAsync();
        await h.EnqueueAsync();
        // The synchronize webhook was lost: the Start step reads the pull request and finds the new head itself.
        h.Forge.PullRequests[Number] = PullRequest(head: Pushed);

        await h.DrainAsync();

        (await h.EntryAsync()).EvictionReason.Should().Be("author-push");
        ShouldOfferTheButton(h.Queue.QueueChecks.Should().ContainSingle(c => c.Sha == Pushed).Which.Check);
    }

    [Fact]
    public async Task A_requeue_after_an_eviction_starts_a_new_check_run_instead_of_reopening_the_completed_one()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(ConflictOutsideTheAllowList());
        await h.EnqueueAsync();
        await h.DrainAsync();
        var ended = h.Queue.QueueChecks.Count;

        await h.EnqueueAsync("maintainer", EMergeQueueCommandSource.Check);

        var (_, check, existing) = h.Queue.QueueChecks[ended];
        check.State.Should().Be(EForgeQueueCheckState.Queued);
        existing.HasValue.Should().BeFalse("GitHub does not reliably reopen a completed check run, and this one carries the button");
    }
}
