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

    /// <param name="state">Neutral, but for an eviction, which ends red (owner, 2026-10-15).</param>
    private static void ShouldOfferTheButton(ForgeQueueCheck check, EForgeQueueCheckState state = EForgeQueueCheckState.Neutral, int number = Number)
    {
        check.State.Should().Be(state);
        check.Actions.Should().Equal([ForgeQueueCheckAction.Enqueue]);
        check.PullRequestNumber.Should().Be(number);
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

    /// <summary>
    /// An eviction ends red, so the pull request list shows its author an X to act on (owner, 2026-10-15), and it keeps the
    /// button: GitHub shows a check's requested actions whatever it concluded.
    /// </summary>
    [Fact]
    public async Task An_eviction_ends_red_keeps_the_button_and_its_title_says_why()
    {
        var h = await HarnessAsync();
        h.Writer.PrepareResults.Enqueue(ConflictOutsideTheAllowList());

        await h.EnqueueAsync();
        await h.DrainAsync();

        (await h.EntryAsync()).State.Should().Be(EMergeQueueEntryState.Evicted);
        var (sha, check, existing) = h.Queue.QueueChecks[^1];
        sha.Should().Be(Head);
        existing.HasValue.Should().BeTrue("the run's own check run ends with the button on it");
        ShouldOfferTheButton(check, EForgeQueueCheckState.Failure);
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
        var evicted = h.Queue.QueueChecks.Last(c => c.Sha == Head).Check;
        ShouldOfferTheButton(evicted, EForgeQueueCheckState.Failure);
        evicted.Title.Should().Be("Evicted: author-push");
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

        // The lane is idle, so its turn starts at once: the first check run of the new run is the one in progress.
        var (_, check, existing) = h.Queue.QueueChecks[ended];
        check.State.Should().Be(EForgeQueueCheckState.InProgress);
        existing.HasValue.Should().BeFalse("GitHub does not reliably reopen a completed check run, and this one carries the button");
    }

    // ── Waiting in line: "Remove from queue" (§0 O32) ────────────────────────────────────────────────

    private const int Second = 8, Third = 9;

    /// <summary>#7 runs; #8 and #9 wait behind it.</summary>
    private async Task<MergeQueueHarness> ALineAsync()
    {
        var h = await HarnessAsync();
        h.Forge.PullRequests[Second] = PullRequest(Second, "6666666666666666666666666666666666666666");
        h.Forge.PullRequests[Third] = PullRequest(Third, "7777777777777777777777777777777777777777");
        await h.EnqueueAsync();
        await h.EnqueueAsync(number: Second);
        await h.EnqueueAsync(number: Third);
        return h;
    }

    private static ForgeQueueCheck LastCheckOn(MergeQueueHarness h, string sha) => h.Queue.QueueChecks.Last(c => c.Sha == sha).Check;

    /// <summary>
    /// GitHub shows a check's buttons only once it has completed, so a pull request waiting in line gets a completed,
    /// neutral check with "Remove from queue". Its turn starts a new run, in progress and without a button.
    /// </summary>
    [Fact]
    public async Task A_pull_request_waiting_in_line_has_a_completed_check_with_the_remove_button_and_its_turn_starts_a_new_run()
    {
        var h = await ALineAsync();

        var waiting = LastCheckOn(h, "6666666666666666666666666666666666666666");
        waiting.State.Should().Be(EForgeQueueCheckState.Neutral);
        waiting.Title.Should().Be("Queued: #2 in line");
        waiting.Actions.Should().Equal([ForgeQueueCheckAction.Dequeue]);
        waiting.PullRequestNumber.Should().Be(Second);
        waiting.Summary.Should().Contain("**Remove from queue** button on this check or a `/dequeue` comment");
        LastCheckOn(h, "7777777777777777777777777777777777777777").Title.Should().Be("Queued: #3 in line");
        h.Queue.QueueChecks.Where(c => c.Sha == Head).Should().OnlyContain(c => c.Check.Actions.Count == 0,
            "#7 started at once: no waiting check, no button");

        // #7 leaves; #8's turn starts on a new run, and #9 moves up the line on the run it has.
        await h.DequeueAsync("maintainer");
        var started = h.Queue.QueueChecks.Last(c => c.Sha == "6666666666666666666666666666666666666666");
        started.ExistingId.HasValue.Should().BeFalse("a completed run is never reopened");
        started.Check.State.Should().Be(EForgeQueueCheckState.InProgress);
        started.Check.Actions.Should().BeEmpty();
        var moved = h.Queue.QueueChecks.Last(c => c.Sha == "7777777777777777777777777777777777777777");
        moved.ExistingId.HasValue.Should().BeTrue();
        moved.Check.Title.Should().Be("Queued: #2 in line");
        moved.Check.Actions.Should().Equal([ForgeQueueCheckAction.Dequeue]);
    }

    /// <summary>The click is <c>/dequeue</c> in all but its source; the waiting run ends with "Add to merge queue" on it.</summary>
    [Fact]
    public async Task A_click_on_remove_from_queue_dequeues_like_dequeue_and_the_add_button_comes_back()
    {
        var h = await ALineAsync();
        var waitingRun = (await h.EntryAsync(Second)).QueueCheckId!.Value;

        await h.DequeueAsync("maintainer", Second, EMergeQueueCommandSource.Check);

        var entry = await h.EntryAsync(Second);
        entry.State.Should().Be(EMergeQueueEntryState.Dequeued);
        entry.EvictionMessage.Should().Be("Removed from the queue by @maintainer.");
        var (_, ended, existing) = h.Queue.QueueChecks.Last(c => c.Sha == "6666666666666666666666666666666666666666");
        existing.Should().Be(waitingRun, "the waiting run ends, completed as it was");
        ended.Title.Should().Be("Dequeued");
        ShouldOfferTheButton(ended, number: Second);
        (await h.QueueDocumentAsync())!.LaneFor("master").Waiting.Should().Equal([MergeQueueEntry.DocumentId(RepositoryId, Third)]);
    }

    /// <summary>Who may click it is who may <c>/dequeue</c>: the author or a writer. Anyone else is noted and ignored, as there.</summary>
    [Fact]
    public async Task A_click_on_remove_from_queue_by_someone_who_may_not_dequeue_is_ignored_like_dequeue()
    {
        var h = await ALineAsync();
        var published = h.Queue.QueueChecks.Count;

        await h.DequeueAsync("stranger", Second, EMergeQueueCommandSource.Check);

        var entry = await h.EntryAsync(Second);
        entry.State.Should().Be(EMergeQueueEntryState.Queued);
        entry.History.Should().Contain(e => e.Text == "@stranger asked to dequeue without write access; ignored");
        h.Queue.QueueChecks.Should().HaveCount(published);
        h.Queue.Comments.Should().BeEmpty();
    }

    // ── The latest check run, for the coverage comment (§0 O32) ─────────────────────────────────────

    private static async Task<MergeQueueLatestCheck?> LatestAsync(MergeQueueHarness h)
    {
        using var session = h.Store.OpenAsyncSession();
        return await session.LoadAsync<MergeQueueLatestCheck>(MergeQueueLatestCheck.DocumentId(RepositoryId, Number));
    }

    /// <summary>
    /// The coverage comment links the pull request's latest queue check: the offer's, then each new run the queue starts.
    /// Updating a run keeps it. The fake forge numbers new check runs from 100.
    /// </summary>
    [Fact]
    public async Task The_latest_check_run_of_the_pull_request_is_recorded_for_the_coverage_comment()
    {
        var h = await HarnessAsync();

        await OfferAsync(h);
        var offered = await LatestAsync(h);
        offered!.CheckRunId.Should().Be(100);
        offered.Sha.Should().Be(Head);
        offered.Number.Should().Be(Number);

        await h.EnqueueAsync();
        (await LatestAsync(h))!.CheckRunId.Should().Be(101, "its turn started a new run");

        await h.DequeueAsync("maintainer");
        (await LatestAsync(h))!.CheckRunId.Should().Be(101, "the run ended on the same check run");
    }
}
