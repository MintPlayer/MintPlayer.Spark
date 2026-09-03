using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// The comment is read by every collaborator on a pull request and gets quoted
/// onward, so what it may and may not contain is a security property, not a
/// formatting preference. These tests hold that line.
/// </summary>
public class PullRequestCommentRendererTests
{
    private const string BaseUrl = "https://coverage.mintplayer.com";
    private const string BadgeToken = "0123456789abcdef0123456789abcdef";

    private static Entities.Repository Repo(bool isPrivate) => new()
    {
        GitHubId = 204431316,
        Name = "MintPlayer.Spark",
        FullName = "MintPlayer/MintPlayer.Spark",
        OwnerLogin = "MintPlayer",
        IsPrivate = isPrivate,
        BadgeToken = BadgeToken,
        DefaultBranch = "master",
    };

    private static Entities.Commit Commit(int? pr = 79, string? baseRef = "master") => new()
    {
        Sha = "79bc284939350991803acc84ced894ade844b9f0",
        Repository = Entities.Repository.DocumentId(204431316),
        Branch = "feature/x",
        PullRequestNumber = pr,
        PullRequestBaseRef = baseRef,
    };

    private static CheckVerdict Verdict(string conclusion, string title) => new(conclusion, null, title, "summary");

    private static CommitAssembly Assembly(string completeness, params string[] reasons) => new()
    {
        Completeness = completeness,
        IncompleteReasons = [.. reasons],
    };

    [Fact]
    public void Marker_is_the_first_line_of_every_body()
    {
        var pending = PullRequestCommentRenderer.RenderPending(Repo(false), "abc1234", BaseUrl);
        var full = PullRequestCommentRenderer.Render(
            Repo(false), Commit(), Verdict("success", "71.4%"), Verdict("neutral", "80.9%"),
            Assembly(CommitAssembly.Complete), BaseUrl, null);

        // Not merely "contains": adoption reads the body, and a marker buried
        // below quoted user text would be a weaker match.
        pending.Should().StartWith(PullRequestCommentRenderer.Marker);
        full.Should().StartWith(PullRequestCommentRenderer.Marker);
    }

    /// <summary>
    /// The whole reason the PR-scoped signature exists. BadgeToken is
    /// manager-only, repo-wide and never expires; publishing it into a comment
    /// would hand every collaborator a credential good for every branch.
    /// </summary>
    [Fact]
    public void Private_repository_uses_the_signature_and_never_the_badge_token()
    {
        var body = PullRequestCommentRenderer.Render(
            Repo(isPrivate: true), Commit(), Verdict("success", "71.4%"), Verdict("neutral", "80.9%"),
            Assembly(CommitAssembly.Complete), BaseUrl, badgeSignature: "deadbeefdeadbeefdeadbeefdeadbeef");

        body.Should().Contain("sig=deadbeefdeadbeefdeadbeefdeadbeef");
        body.Should().NotContain(BadgeToken);
        body.Should().NotContain("token=");
    }

    /// <summary>
    /// No signature and a private repo means there is no image that would
    /// load — and no acceptable way to make one. The numbers still get through.
    /// </summary>
    [Fact]
    public void Private_repository_without_a_signature_omits_the_image_but_keeps_the_numbers()
    {
        var body = PullRequestCommentRenderer.Render(
            Repo(isPrivate: true), Commit(), Verdict("success", "71.4%"), Verdict("neutral", "80.9%"),
            Assembly(CommitAssembly.Complete), BaseUrl, badgeSignature: null);

        body.Should().NotContain("/badge/");
        body.Should().NotContain(BadgeToken);
        body.Should().Contain("71.4%");
        body.Should().Contain("80.9%");
    }

    [Fact]
    public void Public_repository_gets_a_plain_pr_badge_with_no_credential()
    {
        var body = PullRequestCommentRenderer.Render(
            Repo(isPrivate: false), Commit(), Verdict("success", "71.4%"), Verdict("neutral", "80.9%"),
            Assembly(CommitAssembly.Complete), BaseUrl, badgeSignature: null);

        body.Should().Contain($"{BaseUrl}/badge/MintPlayer/MintPlayer.Spark.svg?pr=79");
        body.Should().NotContain("sig=");
        body.Should().NotContain("token=");
    }

    /// <summary>
    /// A partial total that looks like a whole-repository number is the failure
    /// this project already fought once in the badge; the comment must not
    /// reintroduce it.
    /// </summary>
    [Fact]
    public void Partial_assemblies_are_named_with_their_reasons()
    {
        var body = PullRequestCommentRenderer.Render(
            Repo(false), Commit(), Verdict("neutral", "12%"), Verdict("neutral", "50%"),
            Assembly(CommitAssembly.Partial, CommitAssembly.ReasonBaseMismatch, CommitAssembly.ReasonNoBlobIds),
            BaseUrl, null);

        body.Should().Contain("Partial measurement");
        body.Should().Contain("baseMismatch");
        body.Should().Contain("noBlobIds");
    }

    [Fact]
    public void Complete_assemblies_say_nothing_about_completeness()
        => PullRequestCommentRenderer.Render(
                Repo(false), Commit(), Verdict("success", "71.4%"), Verdict("neutral", "80.9%"),
                Assembly(CommitAssembly.Complete), BaseUrl, null)
            .Should().NotContain("Partial measurement");

    [Fact]
    public void The_base_branch_is_named_when_known()
    {
        PullRequestCommentRenderer.Render(
                Repo(false), Commit(baseRef: "master"), Verdict("success", "71.4%"), Verdict("neutral", "80.9%"),
                Assembly(CommitAssembly.Complete), BaseUrl, null)
            .Should().Contain("Compared against `master`");

        // Absent for every PR recorded before the base ref was captured.
        PullRequestCommentRenderer.Render(
                Repo(false), Commit(baseRef: null), Verdict("success", "71.4%"), Verdict("neutral", "80.9%"),
                Assembly(CommitAssembly.Complete), BaseUrl, null)
            .Should().NotContain("Compared against");
    }

    [Fact]
    public void Pending_body_names_the_sha_it_is_waiting_for()
    {
        var body = PullRequestCommentRenderer.RenderPending(Repo(false), "79bc284939350991803acc84ced894ade844b9f0", BaseUrl);

        body.Should().Contain("Waiting for coverage");
        body.Should().Contain("79bc284");
        body.Should().NotContain(BadgeToken);
    }

    /// <summary>The verdicts are the check-runs' own, so the numbers cannot diverge.</summary>
    [Fact]
    public void Both_verdicts_reach_the_table_with_their_conclusion_icons()
    {
        var body = PullRequestCommentRenderer.Render(
            Repo(false), Commit(), Verdict("failure", "48.7% (-3.2%)"), Verdict("success", "91.0%"),
            Assembly(CommitAssembly.Complete), BaseUrl, null);

        body.Should().Contain("❌ 48.7% (-3.2%)");
        body.Should().Contain("✅ 91.0%");
    }

    /// <summary>
    /// A commit with no PR number cannot have a PR badge; rendering one would
    /// produce a ?pr= URL that resolves to nothing.
    /// </summary>
    [Fact]
    public void No_pull_request_number_means_no_badge()
        => PullRequestCommentRenderer.Render(
                Repo(false), Commit(pr: null), Verdict("success", "71.4%"), Verdict("neutral", "80.9%"),
                Assembly(CommitAssembly.Complete), BaseUrl, null)
            .Should().NotContain("/badge/");

    [Fact]
    public void No_base_url_configured_omits_links_rather_than_emitting_relative_ones()
    {
        var body = PullRequestCommentRenderer.Render(
            Repo(false), Commit(), Verdict("success", "71.4%"), Verdict("neutral", "80.9%"),
            Assembly(CommitAssembly.Complete), baseUrl: null, badgeSignature: null);

        body.Should().NotContain("/badge/");
        body.Should().NotContain("full report");
        // The commit link is on github.com and never depended on our base url.
        body.Should().Contain("https://github.com/MintPlayer/MintPlayer.Spark/commit/");
    }
}
