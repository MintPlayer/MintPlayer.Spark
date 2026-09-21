using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// What a fork's coverage is allowed to say on a pull request.
/// </summary>
/// <remarks>
/// <para>
/// Two rules, and both are about not overclaiming. A fork build's check must never be red, because
/// the number came from a workflow the maintainers do not control and any contributor could
/// otherwise mark a pull request as failing. It must never be green either, for the same reason
/// pointed the other way — a pass we cannot stand behind is still a claim. Neutral is the honest
/// answer, and D20 is where that was decided.
/// </para>
/// <para>
/// ⚠️ The failing case is the one that matters. A test that only checks a passing fork build stays
/// green if the force is implemented as "never succeed", which would leave red reachable.
/// </para>
/// </remarks>
public class ForkFeedbackTests
{
    private const string BaseUrl = "https://coverage.mintplayer.com";

    private static Entities.Repository Repo() => new()
    {
        GitHubId = 204431316,
        Name = "MintPlayer.Spark",
        FullName = "MintPlayer/MintPlayer.Spark",
        OwnerLogin = "MintPlayer",
        DefaultBranch = "master",
    };

    private static Entities.Commit Commit(bool fromFork) => new()
    {
        Sha = "79bc284939350991803acc84ced894ade844b9f0",
        Repository = Entities.Repository.DocumentId(EForgeProvider.GitHub, 204431316),
        Branch = "feature/x",
        PullRequestNumber = 79,
        PullRequestBaseRef = "master",
        ContributedFromFork = fromFork,
    };

    private static CheckVerdict Verdict(string conclusion) => new(conclusion, null, "71.4%", "summary");

    [Theory]
    [InlineData("failure")]
    [InlineData("success")]
    [InlineData("neutral")]
    public void A_fork_build_is_always_neutral_whatever_the_gate_concluded(string conclusion)
    {
        var verdict = PublishFeedbackRecipient.ToVerdict(Verdict(conclusion), contributedFromFork: true);

        Assert.Equal(EForgeOutcome.Neutral, verdict.Outcome);
    }

    [Theory]
    [InlineData("failure", EForgeOutcome.Failure)]
    [InlineData("success", EForgeOutcome.Success)]
    [InlineData("neutral", EForgeOutcome.Neutral)]
    public void A_first_party_build_still_maps_its_conclusion_through(string conclusion, EForgeOutcome expected)
    {
        var verdict = PublishFeedbackRecipient.ToVerdict(Verdict(conclusion), contributedFromFork: false);

        Assert.Equal(expected, verdict.Outcome);
    }

    /// <summary>
    /// A check-run list shows titles, not bodies, so the provenance has to survive into the title.
    /// </summary>
    [Fact]
    public void A_fork_builds_title_and_summary_say_where_it_came_from()
    {
        var fork = PublishFeedbackRecipient.ToVerdict(Verdict("success"), contributedFromFork: true);
        var own = PublishFeedbackRecipient.ToVerdict(Verdict("success"), contributedFromFork: false);

        Assert.Contains("fork", fork.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fork", fork.Summary, StringComparison.OrdinalIgnoreCase);

        // The number itself is still there — the provenance annotates it, it does not replace it.
        Assert.Contains("71.4%", fork.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("fork", own.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_comment_says_the_numbers_came_from_a_fork_before_it_shows_them()
    {
        var body = PullRequestCommentRenderer.Render(
            Repo(), Commit(fromFork: true), Verdict("success"), Verdict("success"),
            assembly: null, BaseUrl, badgeSignature: null);

        Assert.Contains("Contributed from a fork", body, StringComparison.Ordinal);

        // Before the table, not after it: a reader who stops at the numbers has still been told.
        var notice = body.IndexOf("Contributed from a fork", StringComparison.Ordinal);
        var table = body.IndexOf("| Check | Result |", StringComparison.Ordinal);
        Assert.True(notice >= 0 && table >= 0 && notice < table,
            "The fork notice must appear above the results table.");
    }

    [Fact]
    public void A_first_party_comment_carries_no_such_notice()
    {
        var body = PullRequestCommentRenderer.Render(
            Repo(), Commit(fromFork: false), Verdict("success"), Verdict("success"),
            assembly: null, BaseUrl, badgeSignature: null);

        Assert.DoesNotContain("Contributed from a fork", body, StringComparison.Ordinal);
    }
}
