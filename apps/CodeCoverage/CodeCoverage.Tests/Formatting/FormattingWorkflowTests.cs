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
            .And.Contain("workflow_dispatch:");
        // The header's rules name `secrets.*` to forbid it; no line of the workflow itself may use one.
        var code = string.Join('\n', template.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
        code.Should().NotContain("secrets.").And.NotContain("id-token");
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

    [Theory]
    [InlineData("format", "mintplayer/format", "Format the repository")]
    [InlineData("lint", "mintplayer/lint", "Lint the repository")]
    public void M5_each_mode_has_its_own_branch_and_title(string mode, string branch, string title)
    {
        FormattingWorkflow.Branch(mode).Should().Be(branch);
        FormattingWorkflow.Title(mode).Should().Be(title);
    }

    private static byte[] Json(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    [Fact]
    public void M5_the_body_lists_the_tools_the_remaining_problems_and_the_queue()
    {
        var report = Json("""{"tools":[{"id":"dotnet-format","version":"11.0.100","files":3},{"id":"prettier","files":1}],"remainingProblems":2,"notes":["eslint: 2 left"]}""");

        var body = FormattingWorkflow.Body("lint", "https://github.com/o/r/actions/runs/1", report, 4, queued: true);

        body.Should().Contain("([run](https://github.com/o/r/actions/runs/1))").And.Contain("4 files changed.")
            .And.Contain("| `dotnet-format` | `11.0.100` | 3 |").And.Contain("| `prettier` | `unpinned` | 1 |")
            .And.Contain("2 problems remain").And.Contain("> eslint: 2 left").And.Contain("Handed to the merge queue");
    }

    [Fact]
    public void M5_the_format_body_never_mentions_remaining_problems()
        => FormattingWorkflow.Body("format", null, Json("""{"remainingProblems":5}"""), 1, queued: false)
            .Should().Contain("1 file changed.").And.NotContain("remain").And.NotContain("[run]").And.NotContain("merge queue");

    [Fact]
    public void M5_the_report_cannot_inject_markup()
    {
        var report = Json("""{"tools":[{"id":"a`|b\n| x","version":"1"}],"notes":["[click](https://evil.example) <img> @team"]}""");

        var body = FormattingWorkflow.Body("format", null, report, 1, queued: false);

        body.Should().Contain("| `a  b   x` | `1` | 0 |");
        body.Should().Contain(@"> \[click\](https://evil.example) \<img\> \@team");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void M5_an_unreadable_report_leaves_the_body_without_one(string text)
    {
        FormattingWorkflow.Report.Parse(Json(text)).Should().BeNull();
        FormattingWorkflow.Body("format", null, Json(text), 2, queued: false).Should().NotContain("| Tool |");
    }

    [Fact]
    public void M5_a_report_over_the_cap_is_not_read()
        => FormattingWorkflow.Report.Parse(new byte[FormattingWorkflow.MaxReportBytes + 1]).Should().BeNull();
}
