using System.Net;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// Moderation (#460 M12) end to end on the QnA demo: votes, delayed crediting, earned privileges, the
/// fraud detector's reversal, flags and the review queue, locks and suspensions. The jobs run through
/// the host's test seams, never on a timer (see <see cref="QnATestHost"/>).
/// </summary>
[Collection(QnAE2ECollection.Name)]
public class QnAModerationTests
{
    private readonly QnATestHost host;

    public QnAModerationTests(QnAE2ECollectionFixture fixture) => host = fixture.Host;

    /// <summary>
    /// Fraud measure 5 and D12 together: an up-vote is pending until the crediting job credits it, and
    /// only credited reputation earns a privilege — <c>Downvote</c> at 10 here.
    /// </summary>
    [Fact]
    public async Task A_vote_counts_toward_a_privilege_only_once_the_crediting_job_credits_it()
    {
        using var author = await host.CreateUserAsync("credit-author");
        using var voter = await host.CreateUserAsync("credit-voter");
        using var other = await host.CreateUserAsync("credit-other");
        var question = await author.Client.AskAsync("How do I pin a RavenDB licence in tests?");
        var answer = await other.Client.AnswerAsync(question.Id!);

        (await author.Client.ReputationAsync()).Privileges.Should().NotContain("Downvote");
        var (early, _) = await author.Client.VoteAsync(AnswerTypeId, answer.Id!, -1);
        early.Should().NotBe(200, "without reputation the author holds no Downvote right");

        var (voted, votedBody) = await voter.Client.VoteAsync(QuestionTypeId, question.Id!, +1);
        voted.Should().Be(200, votedBody.ToString());

        // No recompute: the badge reads pending live, so the vote shows at once (M13 finding, fixed in M14).
        var pending = await author.Client.ReputationAsync();
        pending.Total.Should().Be(0, "nothing is credited before the job runs");
        pending.Pending.Should().Be(10, "the up-vote is on the ledger, waiting");
        pending.Privileges.Should().NotContain("Downvote", "pending reputation earns nothing");

        (await host.RunCreditingAsync()).Should().BeGreaterThan(0);

        var credited = await author.Client.ReputationAsync();
        credited.Total.Should().Be(10);
        credited.Pending.Should().Be(0);
        credited.Privileges.Should().Contain("Downvote");

        var (late, lateBody) = await author.Client.VoteAsync(AnswerTypeId, answer.Id!, -1);
        late.Should().Be(200, $"the privilege applies on the next request: {lateBody}");
    }

    /// <summary>
    /// Fraud measure 6: five votes from one account on one author inside 24 hours is serial voting,
    /// reversed automatically — the reputation goes back to zero and the votes stop counting.
    /// </summary>
    [Fact]
    public async Task Serial_voting_is_reversed_by_the_fraud_detector()
    {
        using var target = await host.CreateUserAsync("serial-target");
        using var voter = await host.CreateUserAsync("serial-voter");
        var ids = new List<string>();
        for (var i = 1; i <= 5; i++)
            ids.Add((await target.Client.AskAsync($"Serial question {i} {Guid.NewGuid():N}")).Id!);

        foreach (var id in ids)
        {
            var (status, body) = await voter.Client.VoteAsync(QuestionTypeId, id, +1);
            status.Should().Be(200, body.ToString());
        }

        await host.RunCreditingAsync();
        (await target.Client.ReputationAsync()).Total.Should().BeGreaterThan(0,
            "three of the five are credited; the pair cap zeroes the other two");

        var report = await host.RunFraudDetectorAsync();
        report.GetProperty("reversedVotes").GetInt32().Should().BeGreaterThanOrEqualTo(5, report.ToString());

        // A reversal of credited entries counts at once: no crediting run between detecting and reading.
        (await target.Client.ReputationAsync()).Total.Should().Be(0, "a reversal compensates every credited entry");
        foreach (var id in ids)
            (await voter.Client.ScoreAsync(QuestionTypeId, id)).Should().Be(0, "a reversal also neutralises the vote");
    }

    /// <summary>A flag opens the post's review case; upholding it credits the flagger at once (+2).</summary>
    [Fact]
    public async Task A_flag_opens_a_review_case_and_upholding_it_credits_the_flagger()
    {
        using var author = await host.CreateUserAsync("flag-author");
        using var flagger = await host.CreateUserAsync("flag-flagger");
        using var moderator = await host.ModeratorAsync();
        var question = await author.Client.AskAsync("Buy cheap watches here " + Guid.NewGuid().ToString("N"));

        var (flagged, flagBody) = await flagger.Client.FlagAsync(QuestionTypeId, question.Id!, "Spam: this is an advertisement.");
        flagged.Should().BeOneOf(200, 204);

        var caseId = $"ModerationCases/flag/{question.Id}";
        await host.WaitForIndexingAsync();
        var queue = (await moderator.PostJsonAsync("/spark/moderation/cases", new { take = 200 })).Result();
        queue.EnumerateArray().Select(c => c.GetProperty("id").GetString()).Should().Contain(caseId);

        var detail = (await moderator.PostJsonAsync("/spark/moderation/case", new { caseId })).Result();
        detail.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);

        await moderator.PostJsonAsync("/spark/moderation/case/decide", new { caseId, decision = "uphold", reason = "Advertisement." });

        (await flagger.Client.ReputationAsync()).Total.Should().Be(2, "an upheld flag is credited at once: a moderator decided it");
    }

    /// <summary>
    /// A lock (S-MOD-D) refuses every write path of an author who otherwise may edit: the edit form, a
    /// delete, a custom action saving through <c>IDatabaseAccess</c>. The page loads without Edit and
    /// Delete; a moderator is exempt; unlocking restores the author.
    /// </summary>
    [Fact]
    public async Task A_locked_question_refuses_every_write_path_of_its_author()
    {
        using var author = await host.CreateUserAsync("lock-author");
        using var moderator = await host.ModeratorAsync();
        var question = await author.Client.AskAsync("A heated question " + Guid.NewGuid().ToString("N"));

        await moderator.PostJsonAsync("/spark/moderation/lock", new { objectTypeId = QuestionTypeId.ToString(), id = question.Id, reason = "Cooling off." });

        var (_, loaded) = await author.Client.LoadRawAsync(QuestionTypeId, question.Id!);
        loaded.DisabledActions().Should().Contain("Edit").And.Contain("Delete");

        var edit = async () => await author.Client.EditAsync(QuestionTypeId, question.Id!, "Title", "Edited while locked");
        (await edit.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var delete = async () => await author.Client.DeletePersistentObjectAsync(QuestionTypeId, question.Id!);
        (await delete.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var parent = await author.Client.GetPersistentObjectAsync(QuestionTypeId, question.Id!);
        var close = async () => await author.Client.ExecuteActionAsync(QuestionTypeId, "CloseQuestion", parent: parent);
        (await close.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await host.LoadAsync<StoredPost>(question.Id!))!.Title.Should().StartWith("A heated question", "no write got through");

        await moderator.EditAsync(QuestionTypeId, question.Id!, "Body", "Edited by a moderator: exempt from the lock.");

        await moderator.PostJsonAsync("/spark/moderation/unlock", new { objectTypeId = QuestionTypeId.ToString(), id = question.Id });
        await author.Client.EditAsync(QuestionTypeId, question.Id!, "Title", "Edited after the unlock");
        (await host.LoadAsync<StoredPost>(question.Id!))!.Title.Should().Be("Edited after the unlock");
    }

    /// <summary>
    /// A suspension (S-MOD-C) blocks writes on the account's next request, even though its session
    /// cookie still authenticates until the next stamp validation; a new sign-in is refused at once.
    /// </summary>
    [Fact]
    public async Task A_suspended_account_is_blocked_from_writing_on_its_next_request()
    {
        using var user = await host.CreateUserAsync("suspended");
        using var moderator = await host.ModeratorAsync();
        var target = await moderator.AskAsync("A question to vote on " + Guid.NewGuid().ToString("N"));

        await moderator.PostJsonAsync("/spark/moderation/suspend", new { userId = user.Id, days = 7, reason = "Repeated spam." });

        var ask = async () => await user.Client.AskAsync("Posted while suspended");
        (await ask.Should().ThrowAsync<SparkClientException>()).Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var (voted, _) = await user.Client.VoteAsync(QuestionTypeId, target.Id!, +1);
        voted.Should().Be(400, "a suspended account cannot vote either");

        using var fresh = host.NewClient();
        var signIn = async () => await fresh.LoginAsync(user.Email, user.Password);
        await signIn.Should().ThrowAsync<SparkClientException>();

        await moderator.PostJsonAsync("/spark/moderation/unsuspend", new { userId = user.Id });
        using var afterwards = host.NewClient();
        await afterwards.LoginAsync(user.Email, user.Password);
        (await afterwards.AskAsync("Posted after the suspension was lifted")).Id.Should().NotBeNullOrEmpty();
    }
}
