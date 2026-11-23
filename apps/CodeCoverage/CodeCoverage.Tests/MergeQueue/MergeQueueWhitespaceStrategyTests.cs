using CodeCoverage.Entities;
using CodeCoverage.Formatting;
using CodeCoverage.Git.MergeQueue;
using CodeCoverage.MergeQueue;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>
/// #657 M6 (docs/coverage_file_formatter_PRD.md D10, D17, D18): <c>coverage.yml</c>'s <c>whitespace</c> strategy and its
/// <c>formatting</c> preset. A whitespace resolution always regenerates with <c>formatting.yml</c>, and the run may write only
/// the files it resolved.
/// </summary>
public class MergeQueueWhitespaceStrategyTests
{
    private static MergeQueueYmlResult Parse(string body, string action = "postRebaseActionName: regenerate.yml")
        => MergeQueueYml.Parse($"mergeQueue:\n  enabled: true\n  {action}\n{body}", "main");

    [Fact]
    public void The_preset_and_the_server_name_one_workflow()
        => AutoResolvePresets.FormattingWorkflowFile.Should().Be(FormattingWorkflow.FileName);

    [Fact]
    public void D10_the_formatting_preset_adds_whitespace_rules_before_the_keep_both_defaults()
    {
        var parsed = Parse("  autoResolveDefaults: [formatting]\n");

        parsed.Errors.Should().BeEmpty();
        var rules = parsed.Section!.AutoResolve;
        var whitespace = rules.TakeWhile(r => r.Strategy == EMergeQueueResolveStrategy.Whitespace).ToList();
        whitespace.Select(r => r.Path).Should().Equal(AutoResolvePresets.Presets["formatting"].Select(p => p.Path));
        whitespace.Should().AllSatisfy(r => (r.Regenerate, r.Action, r.Source).Should().Be(((bool?)true, "formatting.yml", "preset formatting")));
        rules.Skip(whitespace.Count).First().Path.Should().Be("**/*.json", "the keep-both defaults follow");
        parsed.Section.RegeneratedPaths.Should().NotContain("**/*.cs", "a preset never widens the list by its glob");
    }

    [Fact]
    public void D10_the_preset_needs_no_post_rebase_action()
        => Parse("  autoResolveDefaults: [formatting]\n", action: "enqueue: { label: merge-queue }").Errors.Should().BeEmpty();

    [Fact]
    public void D17_an_explicit_whitespace_rule_always_regenerates_with_formatting_yml_and_never_widens_the_list()
    {
        var parsed = Parse("""
              autoResolve:
                - path: "src/**/*.cs"
                  strategy: whitespace
            """);

        parsed.Errors.Should().BeEmpty();
        var rule = parsed.Section!.AutoResolve[0];
        (rule.Strategy, rule.Regenerate, rule.Action).Should().Be((EMergeQueueResolveStrategy.Whitespace, (bool?)true, "formatting.yml"));
        parsed.Section.RegeneratedPaths.Should().NotContain("src/**/*.cs");
    }

    [Theory]
    [InlineData("      regenerate: false\n", "whitespace always regenerates")]
    [InlineData("      action: regenerate.yml\n", "whitespace always regenerates")]
    public void D17_a_whitespace_rule_that_would_skip_the_proof_is_an_error(string extra, string error)
        => Parse($"  autoResolve:\n    - path: \"**/*.cs\"\n      strategy: whitespace\n{extra}").Errors.Should().ContainSingle().Which.Should().Contain(error);

    [Fact]
    public void D17_whitespace_cannot_be_combined_with_post_rebase_never()
        => Parse("  postRebaseWhen: never\n  autoResolveDefaults: [formatting]\n").Errors.Should().Contain(e => e.Contains("postRebaseWhen 'never'"));

    [Fact]
    public void D17_a_whitespace_resolution_dispatches_formatting_yml_alone()
    {
        var config = new MergeQueueConfigSnapshot
        {
            PostRebaseActionName = "regenerate.yml",
            AutoResolve = [new() { Path = "**/*.cs", Strategy = EMergeQueueResolveStrategy.Whitespace, Regenerate = true, Action = "formatting.yml", Source = "preset formatting" }],
        };

        MergeQueueWorkflowGuard.RegenerateWorkflows(config, [new MergeQueueResolution { Path = "src/A.cs", Rule = 0, Regenerate = true, Strategy = "whitespace" }], ["src/A.cs"])
            .Should().Equal(["formatting.yml"]);
    }

    [Theory]
    [InlineData("preset formatting")]
    [InlineData("coverage.yml @ main")]
    public void D18_the_run_may_write_only_the_files_it_resolved(string source)
    {
        var config = new MergeQueueConfigSnapshot
        {
            PostRebaseActionName = "regenerate.yml",
            RegeneratedPaths = ["**/package-lock.json"],
            AutoResolve = [new() { Path = "**/*.cs", Strategy = EMergeQueueResolveStrategy.Whitespace, Regenerate = true, Action = "formatting.yml", Source = source }],
        };

        MergeQueueWorkflowGuard.PublishRegeneratedPaths(config, [new MergeQueueResolution { Path = "src/A.cs", Rule = 0, Regenerate = true, Strategy = "whitespace" }])
            .Should().Equal(["**/package-lock.json", "src/A.cs"]);
    }

    [Fact]
    public void D57_what_the_parser_accepts_the_writer_accepts()
    {
        foreach (var rule in Parse("  autoResolveDefaults: [formatting]\n").Section!.AutoResolve)
            AutoResolveRule.FromWire(rule.Path, rule.Keys, rule.Keys.Count == 0, MergeQueueAutoResolveRule.WireName(rule.Strategy), rule.Validate, rule.Regenerate)
                .Should().NotBeNull(rule.Path);
    }
}
