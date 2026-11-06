using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.Versioning.Tests.Releases;

/// <summary>
/// #568 D2 (#571 M10b): the onboarding pull request lists the old line branches it renames once it merges. The names
/// are the ones MintPlayer's repositories use today (coverage_backport_PRD §5.1.1).
/// </summary>
public class LineRenamesTests
{
    private static readonly HashSet<string> Dotnet = ["dotnet"];
    private static readonly HashSet<string> Angular = ["angular"];

    private static List<string> Renamed(IReadOnlyList<string> branches, IReadOnlySet<string> ecosystems, string defaultBranch = "master")
        => [.. LineRenames.Plan(branches, defaultBranch, ecosystems, ReleaseLinePattern.Default).Renames.Select(r => $"{r.From} -> {r.To}")];

    [Fact]
    public void Each_scheme_in_use_maps_to_the_pattern()
    {
        Renamed(["master", "release/dotnet-6", "release/dotnet-7"], Dotnet)
            .Should().Equal(["release/dotnet-6 -> release/dotnet-6.x", "release/dotnet-7 -> release/dotnet-7.x"]);
        Renamed(["master", "release/6.0", "release/8.0"], Dotnet)
            .Should().Equal(["release/6.0 -> release/dotnet-6.x", "release/8.0 -> release/dotnet-8.x"]);
        Renamed(["master", "release/3.1"], Dotnet).Should().Equal(["release/3.1 -> release/dotnet-3.x"]);
        Renamed(["master", "angular-17", "angular-19"], Angular)
            .Should().Equal(["angular-17 -> release/angular-17.x", "angular-19 -> release/angular-19.x"]);
    }

    [Fact]
    public void Branches_that_only_look_like_a_line_are_left_alone()
    {
        Renamed(["master", "angular-21-def", "angular-21-standalone", "angular-21b", "17", "feature/angular-17", "release/notes"], Angular)
            .Should().BeEmpty();
    }

    [Fact]
    public void A_line_already_in_the_pattern_and_the_default_branch_are_not_renamed()
    {
        Renamed(["release/dotnet-10.x", "release/dotnet-11"], Dotnet, defaultBranch: "release/dotnet-11")
            .Should().BeEmpty();
    }

    [Fact]
    public void A_name_without_an_ecosystem_needs_a_repository_with_one()
    {
        var plan = LineRenames.Plan(["master", "release/6.0"], "master", new HashSet<string> { "dotnet", "angular" }, ReleaseLinePattern.Default);

        plan.Renames.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle(s => s.Branch == "release/6.0").Which.Reason.Should().Contain("which ecosystem");
    }

    [Fact]
    public void An_ecosystem_the_repository_does_not_release_is_skipped()
    {
        var plan = LineRenames.Plan(["master", "angular-17"], "master", Dotnet, ReleaseLinePattern.Default);

        plan.Renames.Should().BeEmpty();
        plan.Skipped.Should().ContainSingle(s => s.Branch == "angular-17").Which.Reason.Should().Contain("angular");
    }

    [Fact]
    public void A_target_that_exists_or_is_claimed_twice_is_skipped()
    {
        var plan = LineRenames.Plan(["master", "release/6.0", "release/dotnet-6", "release/7.0", "release/dotnet-7.x"], "master", Dotnet, ReleaseLinePattern.Default);

        plan.Renames.Should().BeEmpty();
        plan.Skipped.Select(s => s.Branch).Should().BeEquivalentTo(["release/6.0", "release/dotnet-6", "release/7.0"]);
    }

    [Fact]
    public void A_custom_pattern_is_honoured()
    {
        var plan = LineRenames.Plan(["main", "release/6.0"], "main", Dotnet, ReleaseLinePattern.Parse("lines/{ecosystem}/{major}"));

        plan.Renames.Select(r => (r.From, r.To, r.Line.ToString())).Should().Equal([("release/6.0", "lines/dotnet/6", "dotnet-6.x")]);
    }
}
