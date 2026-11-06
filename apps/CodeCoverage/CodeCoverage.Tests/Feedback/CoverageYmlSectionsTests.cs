using CodeCoverage.Feedback;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// #571 M10a (coverage_backport_PRD §8.4 "M10"): the bot adds coverage.yml sections in alphabetical order, so two pull
/// requests that each add one land in the same file whichever merges first.
/// </summary>
public class CoverageYmlSectionsTests
{
    private const string Dependencies = "dependencies:\n  lines: patch\n";
    private const string Versioning = "# Versioning (added by the bot).\nversioning:\n  mode: server\n";

    [Fact]
    public void A_missing_file_gets_the_section_alone()
    {
        CoverageYmlSections.Add(null, "versioning", Versioning).Should().Be(Versioning);
        CoverageYmlSections.Add("", "versioning", "\n" + Versioning + "\n\n").Should().Be(Versioning);
    }

    [Fact]
    public void A_section_goes_before_the_first_key_that_sorts_after_it()
    {
        var yml = "coverage:\n  a: 1\nmergeQueue:\n  enabled: true\n";

        CoverageYmlSections.Add(yml, "dependencies", Dependencies)
            .Should().Be("coverage:\n  a: 1\n\ndependencies:\n  lines: patch\n\nmergeQueue:\n  enabled: true\n");
    }

    [Fact]
    public void The_comments_above_a_key_stay_with_it()
    {
        var yml = "coverage:\n  a: 1\n\n# The queue.\n# Two lines.\nmergeQueue:\n  enabled: true\n";

        CoverageYmlSections.Add(yml, "dependencies", Dependencies)
            .Should().Be("coverage:\n  a: 1\n\ndependencies:\n  lines: patch\n\n# The queue.\n# Two lines.\nmergeQueue:\n  enabled: true\n");
    }

    [Fact]
    public void The_files_header_comment_stays_on_top()
    {
        var yml = "# coverage.yml of acme/widgets\n\nmergeQueue:\n  enabled: true\n";

        CoverageYmlSections.Add(yml, "dependencies", Dependencies)
            .Should().Be("# coverage.yml of acme/widgets\n\ndependencies:\n  lines: patch\n\nmergeQueue:\n  enabled: true\n");
    }

    [Fact]
    public void A_section_that_sorts_last_is_appended()
    {
        CoverageYmlSections.Add("coverage:\n  a: 1", "versioning", Versioning)
            .Should().Be("coverage:\n  a: 1\n\n" + Versioning);
    }

    [Fact]
    public void A_commented_out_section_counts_as_a_key_for_the_order()
    {
        var yml = "# Dependency updates.\n# dependencies:\n#   enabled: true\n\nversioning:\n  mode: server\n";

        CoverageYmlSections.Add(yml, "coverage", "coverage:\n  a: 1\n")
            .Should().Be("coverage:\n  a: 1\n\n" + yml);
        CoverageYmlSections.Add(yml, "mergeQueue", "mergeQueue:\n  enabled: true\n")
            .Should().Be("# Dependency updates.\n# dependencies:\n#   enabled: true\n\nmergeQueue:\n  enabled: true\n\nversioning:\n  mode: server\n");
    }

    [Fact]
    public void A_section_that_is_there_already_changes_nothing()
    {
        var yml = "versioning:\n  mode: local\n";

        CoverageYmlSections.Add(yml, "versioning", Versioning).Should().Be(yml);
        CoverageYmlSections.Has(yml, "versioning").Should().BeTrue();
        CoverageYmlSections.Has("  versioning: 1\n# versioning:\n", "versioning").Should().BeFalse();
    }

    [Fact]
    public void Two_additions_give_the_same_file_in_either_order()
    {
        var yml = "coverage:\n  a: 1\nmergeQueue:\n  enabled: true\n";

        var one = CoverageYmlSections.Add(CoverageYmlSections.Add(yml, "dependencies", Dependencies), "versioning", Versioning);
        var other = CoverageYmlSections.Add(CoverageYmlSections.Add(yml, "versioning", Versioning), "dependencies", Dependencies);

        one.Should().Be(other);
    }

    [Fact]
    public void A_key_goes_into_its_section_in_order()
    {
        var yml = "versioning:\n  mode: server\n#   releaseBranches: [\"release/*.x\"]\n  prerelease: upstream\nzz:\n  a: 1\n";

        CoverageYmlSections.AddKey(yml, "versioning", "lines", "lines: {}")
            .Should().Be("versioning:\n  lines: {}\n  mode: server\n#   releaseBranches: [\"release/*.x\"]\n  prerelease: upstream\nzz:\n  a: 1\n");
        CoverageYmlSections.AddKey(yml, "versioning", "tagPrefix", "tagPrefix: v")
            .Should().Be("versioning:\n  mode: server\n#   releaseBranches: [\"release/*.x\"]\n  prerelease: upstream\n  tagPrefix: v\nzz:\n  a: 1\n");
    }

    [Fact]
    public void A_key_takes_the_sections_indent_and_keeps_a_nested_body_together()
    {
        var yml = "versioning:\n    packages:\n      Acme: { anchor: free }\n";

        CoverageYmlSections.AddKey(yml, "versioning", "lines", "lines:\n  pattern: release/{ecosystem}-{major}.x")
            .Should().Be("versioning:\n    lines:\n      pattern: release/{ecosystem}-{major}.x\n    packages:\n      Acme: { anchor: free }\n");
    }

    [Fact]
    public void A_key_that_is_there_or_a_section_that_cannot_take_it()
    {
        CoverageYmlSections.AddKey("versioning:\n  lines: {}\n", "versioning", "lines", "lines: {}").Should().Be("versioning:\n  lines: {}\n");
        CoverageYmlSections.AddKey("versioning: { mode: server }\n", "versioning", "lines", "lines: {}").Should().BeNull();
        CoverageYmlSections.AddKey("coverage:\n  a: 1\n", "versioning", "lines", "lines: {}").Should().BeNull();
        CoverageYmlSections.AddKey("versioning:\n", "versioning", "lines", "lines: {}").Should().Be("versioning:\n  lines: {}\n");
    }

    [Fact]
    public void A_crlf_file_stays_crlf()
    {
        CoverageYmlSections.Add("coverage:\r\n  a: 1\r\n", "dependencies", Dependencies)
            .Should().Be("coverage:\r\n  a: 1\r\n\r\ndependencies:\r\n  lines: patch\r\n");
    }
}
