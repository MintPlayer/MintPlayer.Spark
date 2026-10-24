using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.PackageVersioning;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Assertions;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>
/// The server host's release-branch cuts (docs/coverage_package_versioning_PRD.md D12): behind the resolved auto-cut policy,
/// a branch that exists already left alone, and the merged pull request told only by the cut that created the branch.
/// </summary>
public class ReleaseBranchCutRecipientTests : CoverageRavenTest
{
    private const string RepositoryId = "Repositories/153";
    private const string AccountId = "Accounts/github/1";
    private const string RunId = "VersioningRuns/github/153/9.1";
    private static readonly string Sha = new('a', 40);

    private static VersioningRunBranchCut Cut(int major, int? pullRequest = 42, string? branch = null, string? commit = null) => new()
    {
        Branch = branch ?? $"release/{major}.x", Major = major, Commit = commit ?? Sha, Packages = ["Acme.Core"], PullRequest = pullRequest,
    };

    private static async Task SeedAsync(IDocumentStore store, bool? accountOn, EAutoCutReleaseBranchPolicy policy, params VersioningRunBranchCut[] cuts)
    {
        using var session = store.OpenAsyncSession();
        if (accountOn is { } on) await session.StoreAsync(new Account { AutoCutReleaseBranches = on }, AccountId);
        await session.StoreAsync(new Repository
        {
            Account = accountOn is null ? null : AccountId, FullName = "acme/widgets", Provider = EForgeProvider.GitHub, AutoCutReleaseBranches = policy,
        }, RepositoryId);
        await session.StoreAsync(new VersioningRun { Repository = RepositoryId, BranchCuts = [.. cuts] }, RunId);
        await session.SaveChangesAsync();
    }

    private static async Task HandleAsync(IDocumentStore store, FakeForgeVersioning forge)
    {
        using var session = store.OpenAsyncSession();
        await new ReleaseBranchCutRecipient(session, [forge], NullLogger<ReleaseBranchCutRecipient>.Instance)
            .HandleAsync(new CutReleaseBranchesMessage { RunId = RunId });
    }

    private static async Task<List<string?>> OutcomesAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        var run = await session.LoadAsync<VersioningRun>(RunId);
        return [.. run!.BranchCuts.Select(c => c.Outcome)];
    }

    [Theory]
    [InlineData(null, EAutoCutReleaseBranchPolicy.Inherit, true)]
    [InlineData(false, EAutoCutReleaseBranchPolicy.Inherit, false)]
    [InlineData(false, EAutoCutReleaseBranchPolicy.Enabled, true)]
    [InlineData(true, EAutoCutReleaseBranchPolicy.Disabled, false)]
    public async Task Branches_are_cut_only_when_the_resolved_policy_is_on(bool? accountOn, EAutoCutReleaseBranchPolicy policy, bool cut)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, accountOn, policy, Cut(10));
        var forge = new FakeForgeVersioning();

        await HandleAsync(store, forge);

        forge.Branches.Should().Equal(cut ? [("release/10.x", Sha)] : []);
        (await OutcomesAsync(store)).Should().Equal(cut ? "created" : "disabled");
    }

    [Fact]
    public async Task An_existing_branch_is_left_alone_and_only_a_created_one_is_announced()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, true, EAutoCutReleaseBranchPolicy.Inherit, Cut(10, 42), Cut(8, 43), Cut(6, pullRequest: null));
        var forge = new FakeForgeVersioning();
        forge.Branches.Add(("release/8.x", new string('b', 40)));

        await HandleAsync(store, forge);
        await HandleAsync(store, forge);

        forge.Branches.Should().Equal(("release/8.x", new string('b', 40)), ("release/10.x", Sha), ("release/6.x", Sha));
        forge.Comments.Select(c => c.Number).Should().Equal(42);
        forge.Comments[0].Body.Should().Be(ReleaseBranchCutRecipient.Comment(Cut(10)));
        forge.Comments[0].Body.Should().Contain("`backport: 10.x`");
        (await OutcomesAsync(store)).Should().Equal("created", "exists", "created");
    }

    [Fact]
    public async Task A_name_or_commit_that_is_not_plain_is_never_sent_to_the_forge()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, true, EAutoCutReleaseBranchPolicy.Inherit,
            Cut(10, branch: "release/*.x"), Cut(9, branch: "release/../9.x"), Cut(8, commit: "HEAD~1"));
        var forge = new FakeForgeVersioning();

        await HandleAsync(store, forge);

        forge.Branches.Should().BeEmpty();
        (await OutcomesAsync(store)).Should().Equal("invalid", "invalid", "invalid");
    }
}
