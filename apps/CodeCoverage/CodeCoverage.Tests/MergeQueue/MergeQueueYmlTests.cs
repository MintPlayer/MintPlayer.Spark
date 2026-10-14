using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using CodeCoverage.MergeQueue;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary><c>coverage.yml</c>'s <c>mergeQueue:</c> section (merge-queue PRD §5.2, O1, O15, O19) and the settings resolution (O11, O12).</summary>
public class MergeQueueYmlTests
{
    private static string RepositoryRootFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "coverage.yml")))
            dir = dir.Parent;
        dir.Should().NotBeNull();
        return File.ReadAllText(Path.Combine(dir!.FullName, "coverage.yml"));
    }

    [Fact]
    public void This_repositorys_own_file_is_usable_as_written()
    {
        var parsed = MergeQueueYml.Parse(RepositoryRootFile(), "master");

        parsed.Errors.Should().BeEmpty();
        parsed.IsUsable.Should().BeTrue();
        var section = parsed.Section!;
        section.Branches.Should().Equal(["master"]);
        section.Label.Should().Be("merge-queue");
        section.PostRebaseActionName.Should().Be("regenerate.yml");
        section.PostRebaseTimeoutMinutes.Should().Be(20);
        section.PostRebaseWhen.Should().Be(EPostRebaseWhen.AutoResolved);
        section.AutoResolve.Select(r => r.Path).Should().Equal(["**/App_Data/modelHashes.json", "**/App_Data/securityPosture.txt"]);
        section.AutoResolve.Should().AllSatisfy(r => r.Keys.Should().BeEmpty("O19: these files resolve on any line"));
        section.RegeneratedPaths.Should().Contain("**/App_Data/security.json");
    }

    [Fact]
    public void No_file_or_no_section_is_no_queue_and_no_error()
    {
        var none = MergeQueueYml.Parse(null, "main");
        none.Section.Should().BeNull();
        none.Errors.Should().BeEmpty();
        var gateOnly = MergeQueueYml.Parse("gate:\n  blocking: true\n", "main");
        gateOnly.Section.Should().BeNull();
        gateOnly.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Defaults_take_the_default_branch_and_name_their_source()
    {
        var parsed = MergeQueueYml.Parse("mergeQueue:\n  enabled: true\n", "trunk");

        parsed.IsUsable.Should().BeTrue();
        parsed.Section!.Branches.Should().Equal(["trunk"]);
        parsed.Section.Values.Should().Contain(v => v.Key == "branches" && v.Value == "trunk" && v.Source == "default");
        parsed.Section.Values.Should().Contain(v => v.Key == "enabled" && v.Source == "coverage.yml");
    }

    [Fact]
    public void Take_base_without_a_regenerate_workflow_is_an_error()
        => MergeQueueYml.Parse("""
            mergeQueue:
              enabled: true
              autoResolve:
                - path: "**/modelHashes.json"
            """, "main").Errors.Should().ContainSingle().Which.Should().Contain("postRebaseActionName");

    [Fact]
    public void Json_union_is_no_longer_a_strategy()
        => MergeQueueYml.Parse("""
            mergeQueue:
              enabled: true
              postRebaseActionName: regenerate.yml
              regeneratedPaths: ["**/x.json"]
              autoResolve:
                - path: "**/translations.json"
                  strategy: json-union
            """, "main").Errors.Should().ContainSingle().Which.Should().Contain("only take-base");

    [Theory]
    [InlineData("postRebaseTimeout: 90s", 2)]
    [InlineData("postRebaseTimeout: 1h", 60)]
    [InlineData("postRebaseTimeout: 45", 45)]
    public void Durations_read_in_minutes(string line, int minutes)
        => MergeQueueYml.Parse($"mergeQueue:\n  enabled: true\n  {line}\n", "main").Section!.PostRebaseTimeoutMinutes.Should().Be(minutes);

    [Theory]
    [InlineData("postRebaseTimeout: soon")]
    [InlineData("postRebaseTimeout: 9h")]
    [InlineData("mergeMethod: rebase")]
    [InlineData("postRebaseWhen: sometimes")]
    [InlineData("postRebaseActionName: ../evil.yml")]
    [InlineData("checks: { retryFlaky: 7 }")]
    [InlineData("branches: []")]
    public void Invalid_values_are_errors_not_exceptions(string line)
        => MergeQueueYml.Parse($"mergeQueue:\n  enabled: true\n  {line}\n", "main").Errors.Should().NotBeEmpty();

    [Fact]
    public void Unreadable_yaml_is_an_error()
        => MergeQueueYml.Parse("mergeQueue: [unclosed", "main").Errors.Should().ContainSingle();

    [Fact]
    public void Key_scoped_rules_keep_their_keys()
        => MergeQueueYml.Parse("""
            mergeQueue:
              enabled: true
              postRebaseActionName: regenerate.yml
              regeneratedPaths: ["**/modelHashes.json"]
              autoResolve:
                - path: "**/modelHashes.json"
                  strategy: take-base
                  keys: [modelHash, contextRoots]
            """, "main").Section!.AutoResolve.Single().Keys.Should().Equal(["modelHash", "contextRoots"]);

    // ── Settings resolution (O11, O12) ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_approval_floor_defaults_to_external_authors_and_a_repository_may_override_it()
    {
        Entities.Repository.ResolveMergeQueueApprovalFloor(new Entities.Repository(), new Account())
            .Should().Be((EMergeQueueApprovalFloor.ExternalAuthors, "organization"));
        Entities.Repository.ResolveMergeQueueApprovalFloor(new Entities.Repository(), new Account { MergeQueueApprovalFloor = EMergeQueueApprovalFloor.Always })
            .Should().Be((EMergeQueueApprovalFloor.Always, "organization"));
        Entities.Repository.ResolveMergeQueueApprovalFloor(
                new Entities.Repository { MergeQueueApprovalFloor = EMergeQueueApprovalFloorPolicy.RepositoryRules },
                new Account { MergeQueueApprovalFloor = EMergeQueueApprovalFloor.Always })
            .Should().Be((EMergeQueueApprovalFloor.RepositoryRules, "repository"));
    }

    [Fact]
    public void Check_exclusions_inherit_unless_the_repository_overrides_even_with_an_empty_list()
    {
        var account = new Account { MergeQueueCheckExclusions = [" scan ", "lint", "scan", ""] };

        var (inherited, inheritedSource) = Entities.Repository.ResolveMergeQueueCheckExclusions(new Entities.Repository(), account);
        inherited.Should().Equal(["lint", "scan"]);
        inheritedSource.Should().Be("organization");

        var (overridden, overriddenSource) = Entities.Repository.ResolveMergeQueueCheckExclusions(
            new Entities.Repository { MergeQueueCheckExclusionsPolicy = EMergeQueueCheckExclusionsPolicy.Override }, account);
        overridden.Should().BeEmpty();
        overriddenSource.Should().Be("repository");
    }
}
