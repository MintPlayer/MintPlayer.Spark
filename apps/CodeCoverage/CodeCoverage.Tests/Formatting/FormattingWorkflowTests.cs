using CodeCoverage.Formatting;
using Xunit;

namespace CodeCoverage.Tests.Formatting;

/// <summary>#657 D11, D20: the formatting template, and the <c>exclude</c> list the server reads back from a repository's copy.</summary>
public class FormattingWorkflowTests
{
    [Fact]
    public void The_template_keeps_the_security_rules_and_takes_every_dispatch_input()
    {
        var template = FormattingWorkflow.Template();

        template.Should().Contain("permissions:\n  contents: read\n").And.Contain("persist-credentials: false")
            .And.Contain("workflow_dispatch:").And.NotContain("secrets.").And.NotContain("id-token");
        foreach (var input in new[] { "mode", "expected_sha", "pr_number", "queue_run_id", "merge_base", "ours_sha", "theirs_sha" })
            template.Should().Contain($"      {input}:\n", $"the dispatch sends {input}");
        template.Should().NotContain("      exclude:\n        description", "D11: exclude is set in the workflow, not dispatched");
    }

    [Fact]
    public void The_template_s_own_exclude_list_is_read_back()
        => FormattingWorkflow.Exclude(FormattingWorkflow.Template()).Should().Equal("**/*.g.cs", "**/*.Designer.cs");

    [Fact]
    public void Exclude_reads_every_step_that_calls_the_action_and_drops_comments()
    {
        const string yml = """
            jobs:
              a:
                steps:
                  - uses: actions/checkout@v7
                    with:
                      exclude: not/this/**
                  - uses: MintPlayer/MintPlayer.Spark/.github/actions/format@format-v1
                    with:
                      exclude: |
                        # generated
                        gen/**

                        vendor/**
              b:
                steps:
                  - uses: MintPlayer/MintPlayer.Spark/.github/actions/format@0123456789abcdef0123456789abcdef01234567
                    with:
                      exclude: "gen/**"
            """;

        FormattingWorkflow.Exclude(yml.ReplaceLineEndings("\n")).Should().Equal("gen/**", "vendor/**");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("jobs: [")]
    [InlineData("name: x\n")]
    [InlineData("jobs:\n  a:\n    steps:\n      - uses: MintPlayer/MintPlayer.Spark/.github/actions/format@format-v1\n")]
    public void Exclude_is_empty_when_there_is_nothing_to_read(string? yml)
        => FormattingWorkflow.Exclude(yml).Should().BeEmpty();
}
