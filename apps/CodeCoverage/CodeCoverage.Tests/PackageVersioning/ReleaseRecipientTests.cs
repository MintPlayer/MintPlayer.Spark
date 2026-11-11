using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Releases;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>Segment P: which lines and which commit a <c>/release</c> takes (P2, P4), and the comment it posts.</summary>
public class ReleaseRecipientTests
{
    private static readonly ReleaseLinePattern Pattern = ReleaseLinePattern.Default;

    private static readonly IReadOnlyList<ReleaseLine> Current
        = Pattern.CurrentLines(["release/dotnet-10.x", "release/dotnet-11.x", "release/angular-22.x"]);

    private static ReleaseCommentRequest Request(bool all = false, string? sha = null, params string[] lines) => new(lines, all, sha, []);

    private static ForgeWorkflowRun Run(long id, string sha, string? conclusion, string status = "completed")
        => new(id, status, conclusion, sha, "master", $"https://example.invalid/runs/{id}");

    [Fact]
    public void Without_names_every_current_line_is_selected()
    {
        ReleaseRecipient.Select(Request(), Pattern, Current)!.Select(l => l.ToString()).Should().Equal(["angular-22.x", "dotnet-11.x"]);
        ReleaseRecipient.Select(Request(all: true), Pattern, Current)!.Should().HaveCount(2);
    }

    [Fact]
    public void A_named_line_is_taken_by_its_name_or_its_branch_once()
    {
        ReleaseRecipient.Select(Request(false, null, "dotnet-11.x", "release/dotnet-11.x"), Pattern, Current)!
            .Select(l => l.ToString()).Should().Equal(["dotnet-11.x"]);
    }

    [Theory]
    [InlineData("dotnet-10.x")]
    [InlineData("php-1.x")]
    [InlineData("nonsense")]
    public void A_past_or_unknown_line_refuses_the_request(string name)
        => ReleaseRecipient.Select(Request(false, null, name), Pattern, Current).Should().BeNull();

    [Fact]
    public void The_newest_successful_run_is_green_or_the_one_whose_sha_the_comment_names()
    {
        IReadOnlyList<ForgeWorkflowRun> runs =
        [
            Run(5, "eeee000000000000000000000000000000000000", null, "in_progress"),
            Run(4, "dddd000000000000000000000000000000000000", "failure"),
            Run(3, "cccc000000000000000000000000000000000000", "success"),
            Run(2, "bbbb000000000000000000000000000000000000", "success"),
        ];

        ReleaseRecipient.Green(runs, null)!.Id.Should().Be(3);
        ReleaseRecipient.Green(runs, "BBBB000")!.Id.Should().Be(2);
        ReleaseRecipient.Green(runs, "dddd000").Should().BeNull("its run failed");
    }

    [Fact]
    public void The_report_lists_each_line_the_line_only_paths_and_the_newer_commits_left_out()
    {
        IReadOnlyList<ForgeWorkflowRun> runs =
        [
            Run(4, "dddd000000000000000000000000000000000000", "failure"),
            Run(3, "cccc000000000000000000000000000000000000", "success"),
        ];
        var result = new GitWriterPromoteResult(GitWriterPromoteResult.Promoted, null,
        [
            new("release/dotnet-11.x", GitWriterPromoteLineResult.Promoted, "1111000000000000000000000000000000000000", [], []),
            new("release/angular-22.x", GitWriterPromoteLineResult.LineOnly, "2222000000000000000000000000000000000000", ["README.md"], ["3333 line: readme"]),
        ], null, 10);

        var text = ReleaseRecipient.Report(new Repository { FullName = "acme/widgets" }, "master", "release.yml", runs[1], runs, result);

        text.Should().Contain("[cccc00000000](https://github.com/acme/widgets/commit/cccc000000000000000000000000000000000000)")
            .And.Contain("- `release/dotnet-11.x`: promoted (111100000000)")
            .And.Contain("- `release/angular-22.x`: **not promoted**")
            .And.Contain("- `README.md`").And.Contain("- 3333 line: readme")
            .And.Contain("1 newer commit on master is left out");
    }

    [Fact]
    public void A_named_older_commit_does_not_call_newer_green_commits_not_passed()
    {
        IReadOnlyList<ForgeWorkflowRun> runs =
        [
            Run(3, "cccc000000000000000000000000000000000000", "success"),
            Run(2, "bbbb000000000000000000000000000000000000", "success"),
        ];
        var result = new GitWriterPromoteResult(GitWriterPromoteResult.Promoted, null, [], null, 10);

        ReleaseRecipient.Report(new Repository { FullName = "acme/widgets" }, "master", "release.yml", runs[1], runs, result)
            .Should().NotContain("left out");
    }
}
