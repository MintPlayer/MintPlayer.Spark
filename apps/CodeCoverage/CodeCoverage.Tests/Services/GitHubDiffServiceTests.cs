using CodeCoverage.Services;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The hunk-header line mapping patch coverage stands on: added lines are
/// numbered in new-file space, context advances the counter, deletions don't.
/// One line wrong here and every patch verdict is wrong by one line.
/// </summary>
public class GitHubDiffServiceTests
{
    [Fact]
    public void Added_lines_are_numbered_from_the_hunk_headers_new_start()
    {
        // Line 3 modified (deletion + addition), lines 6-7 inserted.
        const string patch =
            "@@ -1,4 +1,5 @@\n" +
            " line1\n" +
            " line2\n" +
            "-old3\n" +
            "+new3\n" +
            " line4\n" +
            "+inserted5\n" +
            "@@ -10,2 +11,3 @@\n" +
            " line11\n" +
            "+inserted12\n" +
            " line13";

        GitHubDiffService.AddedLines(patch).Should().Equal(3, 5, 12);
    }

    [Fact]
    public void A_pure_deletion_adds_nothing()
    {
        const string patch =
            "@@ -5,3 +5,2 @@\n" +
            " keep\n" +
            "-gone\n" +
            " keep2";

        GitHubDiffService.AddedLines(patch).Should().BeEmpty();
    }

    [Fact]
    public void A_new_file_counts_every_line()
    {
        const string patch =
            "@@ -0,0 +1,3 @@\n" +
            "+a\n" +
            "+b\n" +
            "+c";

        GitHubDiffService.AddedLines(patch).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void No_newline_marker_and_file_headers_do_not_shift_the_count()
    {
        const string patch =
            "--- a/f.ts\n" +
            "+++ b/f.ts\n" +
            "@@ -1,2 +1,2 @@\n" +
            " ctx\n" +
            "+last\n" +
            "\\ No newline at end of file";

        GitHubDiffService.AddedLines(patch).Should().Equal(2);
    }

    [Fact]
    public void Missing_patch_yields_no_lines()
        => GitHubDiffService.AddedLines(null).Should().BeEmpty();
}
