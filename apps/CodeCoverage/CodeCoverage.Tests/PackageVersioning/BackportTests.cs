using CodeCoverage.Forge;
using CodeCoverage.PackageVersioning;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>#153 M10 (D10): how a backport is asked for, which branch a line names, and what a conflict tells the author.</summary>
public class BackportTests
{
    private static readonly IReadOnlyList<string> Patterns = ["release/*.x"];

    [Theory]
    [InlineData("backport: 8.x", "8.x")]
    [InlineData("backport 8", "8")]
    [InlineData("Backport:release/8.x", "release/8.x")]
    [InlineData("backported", null)]
    [InlineData("backport:", null)]
    [InlineData("backport: 8.x; rm", null)]
    [InlineData("bug", null)]
    public void A_label_names_one_line(string label, string? line)
        => BackportCommand.FromLabel(label).Should().Be(line);

    [Fact]
    public void A_comment_names_its_lines_on_the_first_line_only()
    {
        BackportCommand.FromComment("/backport 8.x, 9.x 8.x\nand more text").Should().Equal("8.x", "9.x");
        BackportCommand.FromComment("please /backport 8.x").Should().BeNull();
        BackportCommand.FromComment("/backport").Should().BeNull();
        BackportCommand.FromComment("/backport 8.x $(id)").Should().BeNull();
        BackportCommand.FromComment("/backport " + string.Join(' ', Enumerable.Range(1, 11))).Should().BeNull();
    }

    [Theory]
    [InlineData("8", "release/8.x")]
    [InlineData("8.x", "release/8.x")]
    [InlineData("release/7.x", "release/7.x")]
    [InlineData("main", null)]
    [InlineData("feature/8.x", null)]
    public void A_line_resolves_only_through_the_release_patterns(string line, string? branch)
        => BackportRecipient.Resolve(Patterns, line).Should().Be(branch);

    [Fact]
    public void A_conflict_names_the_files_and_the_commands_per_line()
    {
        var result = new GitWriterBackportResult(GitWriterBackportResult.Conflicted, null, "merge", "b4se", [
            new("release/1.x", "backport/7-1.x", null, null, []),
            new("release/2.x", "backport/7-2.x", null, null, ["README.md"]),
        ], null, 12);

        var comment = BackportRecipient.ConflictComment("c0ffee", result);

        comment.Should().Contain("`release/2.x` conflicts on:").And.Contain("- `README.md`")
            .And.Contain("git switch -c backport/7-2.x origin/release/2.x").And.Contain("git cherry-pick -x -m 1 c0ffee")
            .And.NotContain("release/1.x");
    }

    [Fact]
    public void A_rebase_conflict_picks_the_whole_run()
    {
        var result = new GitWriterBackportResult(GitWriterBackportResult.Conflicted, null, "rebase", "b4se",
            [new("release/2.x", "backport/7-2.x", null, null, ["a.txt"])], null, 1);

        BackportRecipient.ConflictComment("c0ffee", result).Should().Contain("git cherry-pick -x b4se..c0ffee");
    }
}
