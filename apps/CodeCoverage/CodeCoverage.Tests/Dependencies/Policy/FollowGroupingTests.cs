using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// docs/code-coverage/dependency-companion-pins-PRD.md §7.1a F2: the lookup gives a follower record its leader's
/// <c>depName</c>, so the planner groups it with the leader by name (D-C1) and the two move in one group, each to the same
/// release of the leader. The candidate values are the ones the lookup's <c>follow.ts</c> derives (S7).
/// </summary>
public class FollowGroupingTests
{
    private const string Leader = "mcr.microsoft.com/playwright";

    private static LookupRecord Docker(string manager, string packageFile, string current, params LookupCandidate[] candidates)
        => Record(Leader, current, candidates) with
        {
            Ecosystem = "docker", Manager = manager, Datasource = "docker", PackageFile = packageFile, LockFiles = [], DepType = null, CurrentVersion = null,
        };

    [Fact]
    public void A_follower_moves_in_its_leaders_group_to_the_same_release()
    {
        var lookup = Lookup(
            Docker("dockerfile", "apps/SeoTester/chromium/Dockerfile", "v1.63.0-noble",
                Candidate(EUpdateType.Minor, "1.64.1", value: "v1.64.1-noble"), Candidate(EUpdateType.Minor, "1.65.1", value: "v1.65.1-noble")),
            Docker(DependencyFollowManager, "apps/SeoTester/docker-compose.yml", "1.63.0",
                Candidate(EUpdateType.Minor, "1.64.1"), Candidate(EUpdateType.Minor, "1.65.1")));

        var outcome = Evaluate(lookup, Config("dependencies:\n  autoMerge: { default: major }\n"));

        outcome.Groups.Should().HaveCount(1);
        // The engine takes each member's first allowed candidate in lookup order; the follower's mirror the leader's, so
        // both land on the same release.
        outcome.Groups[0].Items.Select(i => (i.Record.PackageFile, i.ToPlanItem().NewValue)).Should().Equal(
            ("apps/SeoTester/chromium/Dockerfile", "v1.64.1-noble"),
            ("apps/SeoTester/docker-compose.yml", "1.64.1"));
    }

    /// <summary>The writer's <c>DependencyPatchValidator.FollowManager</c>; the server does not reference the writer.</summary>
    private const string DependencyFollowManager = "follow";
}
