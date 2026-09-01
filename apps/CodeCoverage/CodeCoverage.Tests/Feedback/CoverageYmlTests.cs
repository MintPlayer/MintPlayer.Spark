using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// coverage.yml overrides the settings document per field: only keys the file
/// sets win, malformed files change nothing (the error is surfaced, the stored
/// policy stands), and invalid enum values are ignored rather than trusted.
/// </summary>
public class CoverageYmlTests
{
    private static readonly GateSettings Stored = new()
    {
        ProjectMode = "auto",
        ProjectThreshold = 1,
        PatchTarget = 70,
        PatchThreshold = 2,
        Blocking = false,
    };

    [Fact]
    public void Only_the_fields_the_file_sets_are_overridden()
    {
        var merged = CoverageYml.Merge(Stored, "gate:\n  blocking: true\n  patchTarget: 85\n", out var error);

        error.Should().BeNull();
        merged.Blocking.Should().BeTrue();
        merged.PatchTarget.Should().Be(85);
        merged.ProjectMode.Should().Be("auto", "untouched fields keep the stored value");
        merged.ProjectThreshold.Should().Be(1);
        merged.PatchThreshold.Should().Be(2);
    }

    [Fact]
    public void A_missing_or_empty_file_changes_nothing()
    {
        CoverageYml.Merge(Stored, null, out _).Should().BeSameAs(Stored);
        CoverageYml.Merge(Stored, "", out _).Should().BeSameAs(Stored);
    }

    [Fact]
    public void A_malformed_file_keeps_the_stored_policy_and_surfaces_the_error()
    {
        var merged = CoverageYml.Merge(Stored, "gate: [not: valid", out var error);

        merged.Should().BeSameAs(Stored);
        error.Should().Contain("coverage.yml ignored");
    }

    [Fact]
    public void Invalid_enum_values_are_ignored_not_trusted()
    {
        var merged = CoverageYml.Merge(Stored, "gate:\n  projectMode: yolo\n  projectBasis: everything\n", out var error);

        error.Should().BeNull();
        merged.ProjectMode.Should().Be("auto");
        merged.ProjectBasis.Should().Be(Stored.ProjectBasis);
    }

    [Fact]
    public void Unknown_keys_are_tolerated()
    {
        var merged = CoverageYml.Merge(Stored, "gate:\n  blocking: true\n  futureKnob: 3\nother: {}\n", out var error);

        error.Should().BeNull();
        merged.Blocking.Should().BeTrue();
    }
}
