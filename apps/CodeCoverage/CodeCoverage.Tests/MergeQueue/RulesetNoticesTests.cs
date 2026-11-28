using MintPlayer.Spark.Abstractions;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.MergeQueue;
using CodeCoverage.Tests.CommitGraph;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// #714 Dec5, Dec11: what the managers' bell is told about, and when it stops applying. Each notice is one document per
/// repository and kind, written in the caller's session.
/// </summary>
public class RulesetNoticesTests : CoverageRavenTest
{
    private static readonly DateTimeOffset Start = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly string RepositoryId = Entities.Repository.DocumentId(EForgeProvider.GitHub, 4711);
    private static readonly Entities.Repository Repo = new()
    {
        Id = Id<Entities.Repository>.FromTrusted(RepositoryId),
        FullName = "acme/widget",
    };
    private readonly MutableTime time = new(Start);

    private static ForgeBranchRules Requiring(params ForgeRequiredCheck[] checks)
        => new(true, [.. checks.Select(check => check.Context)], 0, false, false, false, false,
            [new ForgeRuleset(2, "Reviews and checks", ["required_status_checks"], EForgeRulesetBypass.Never, checks)], EForgeClassicProtection.None);

    private static ForgeBranchRules RequiringNames(params string[] names) => Requiring([.. names.Select(name => new ForgeRequiredCheck(name))]);

    private async Task WriteAsync(Raven.Client.Documents.IDocumentStore store, Func<RulesetNotices, Task> write)
    {
        using var session = store.OpenAsyncSession();
        await write(new RulesetNotices(session, time));
        await session.SaveChangesAsync();
    }

    private static async Task<RulesetNotice?> LoadAsync(Raven.Client.Documents.IDocumentStore store, ERulesetNoticeKind kind)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<RulesetNotice>(RulesetNotice.DocumentId(RepositoryId, kind));
    }

    private static async Task MarkNotifiedAsync(Raven.Client.Documents.IDocumentStore store, ERulesetNoticeKind kind, string user)
    {
        using var session = store.OpenAsyncSession();
        (await session.LoadAsync<RulesetNotice>(RulesetNotice.DocumentId(RepositoryId, kind))).NotifiedUsers.Add(user);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task The_same_drift_seen_again_keeps_who_was_told_and_a_new_one_tells_everyone_again()
    {
        using var store = GetDocumentStore();
        await WriteAsync(store, n => n.RaiseMissingCheckAsync(Repo, "master", ["test"], ["build", "test"], 7));
        await MarkNotifiedAsync(store, ERulesetNoticeKind.MissingCheck, "Users/1");

        time.Now = Start.AddMinutes(30);
        await WriteAsync(store, n => n.RaiseMissingCheckAsync(Repo, "master", ["test"], ["build", "lint"], 8));

        var same = (await LoadAsync(store, ERulesetNoticeKind.MissingCheck))!;
        same.NotifiedUsers.Should().Equal(["Users/1"], "a manager is not told twice about one drift");
        same.RaisedAtUtc.Should().Be(Start.UtcDateTime);
        same.PullRequest.Should().Be(8);
        same.ReportedOnHead.Should().Equal(["build", "lint"]);
        same.RepositoryName.Should().Be("acme/widget");

        await WriteAsync(store, n => n.RaiseMissingCheckAsync(Repo, "master", ["test", "tests"], ["build"], 9));

        var next = (await LoadAsync(store, ERulesetNoticeKind.MissingCheck))!;
        next.Checks.Should().Equal(["test", "tests"]);
        next.NotifiedUsers.Should().BeEmpty("the next drift is news");
        next.RaisedAtUtc.Should().Be(Start.AddMinutes(30).UtcDateTime);
    }

    [Fact]
    public async Task A_resolved_drift_seen_again_is_raised_again()
    {
        using var store = GetDocumentStore();
        await WriteAsync(store, n => n.RaiseMissingCheckAsync(Repo, "master", ["test"], ["build"], 7));
        await MarkNotifiedAsync(store, ERulesetNoticeKind.MissingCheck, "Users/1");
        await WriteAsync(store, n => n.ResolveAllAsync(RepositoryId));
        (await LoadAsync(store, ERulesetNoticeKind.MissingCheck))!.IsOpen().Should().BeFalse();

        await WriteAsync(store, n => n.RaiseMissingCheckAsync(Repo, "master", ["test"], ["build"], 7));

        var again = (await LoadAsync(store, ERulesetNoticeKind.MissingCheck))!;
        again.IsOpen().Should().BeTrue();
        again.NotifiedUsers.Should().BeEmpty();
    }

    [Fact]
    public async Task Rules_that_still_require_the_name_or_belong_to_another_branch_leave_the_notice_open()
    {
        using var store = GetDocumentStore();
        await WriteAsync(store, n => n.RaiseMissingCheckAsync(Repo, "master", ["test"], ["build"], 7));

        await WriteAsync(store, n => n.ResolveFixedAsync(Repo, "master", RequiringNames("build", "test")));
        (await LoadAsync(store, ERulesetNoticeKind.MissingCheck))!.IsOpen().Should().BeTrue("`test` is still required");

        await WriteAsync(store, n => n.ResolveFixedAsync(Repo, "release/1.x", RequiringNames("build")));
        (await LoadAsync(store, ERulesetNoticeKind.MissingCheck))!.IsOpen().Should().BeTrue("another branch's rules say nothing about master's");

        await WriteAsync(store, n => n.ResolveFixedAsync(Repo, "master", RequiringNames("build")));
        (await LoadAsync(store, ERulesetNoticeKind.MissingCheck))!.ResolvedAtUtc.Should().Be(Start.UtcDateTime);
    }

    [Fact]
    public async Task A_pin_mismatch_is_resolved_once_no_rule_pins_the_name_to_another_App()
    {
        using var store = GetDocumentStore();
        await WriteAsync(store, n => n.RaisePinMismatchAsync(Repo, "master", new Dictionary<string, long> { ["build"] = 15368 }, 7));

        await WriteAsync(store, n => n.ResolveFixedAsync(Repo, "master", Requiring(new ForgeRequiredCheck("build", 999))));
        (await LoadAsync(store, ERulesetNoticeKind.PinMismatch))!.IsOpen().Should().BeTrue();

        await WriteAsync(store, n => n.ResolveFixedAsync(Repo, "master", Requiring(new ForgeRequiredCheck("build", 15368))));
        (await LoadAsync(store, ERulesetNoticeKind.PinMismatch))!.IsOpen().Should().BeFalse("pinned to the App that reports it");
    }

    [Fact]
    public async Task The_summary_job_is_offered_once_per_repository()
    {
        using var store = GetDocumentStore();
        await WriteAsync(store, n => n.OfferSummaryJobAsync(Repo, "master", RequiringNames("coverage/patch", "build")));
        (await LoadAsync(store, ERulesetNoticeKind.SummaryJob)).Should().BeNull("one job and the App's own checks is the summary-job shape");

        await WriteAsync(store, n => n.OfferSummaryJobAsync(Repo, "master", RequiringNames("coverage/patch", "build", "test")));
        var offer = (await LoadAsync(store, ERulesetNoticeKind.SummaryJob))!;
        offer.Checks.Should().Equal(["build", "test"]);
        offer.PullRequest.HasValue.Should().BeFalse();

        // Declined: the setup ran without switching. The rules still list every job, and nobody is asked again.
        await WriteAsync(store, n => n.ResolveAllAsync(RepositoryId));
        await WriteAsync(store, n => n.OfferSummaryJobAsync(Repo, "master", RequiringNames("build", "test", "lint")));
        var declined = (await LoadAsync(store, ERulesetNoticeKind.SummaryJob))!;
        declined.IsOpen().Should().BeFalse();
        declined.Checks.Should().Equal(["build", "test"]);
    }

    [Fact]
    public async Task An_open_offer_is_resolved_once_the_branch_requires_one_job()
    {
        using var store = GetDocumentStore();
        await WriteAsync(store, n => n.OfferSummaryJobAsync(Repo, "master", RequiringNames("build", "test")));

        await WriteAsync(store, n => n.OfferSummaryJobAsync(Repo, "master", RequiringNames("ci", "coverage/project", "Line policy")));

        (await LoadAsync(store, ERulesetNoticeKind.SummaryJob))!.IsOpen().Should().BeFalse();
    }
}
