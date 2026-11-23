using CodeCoverage.Onboarding;
using CodeCoverage.Versioning.Changelog;
using CodeCoverage.Versioning.Graph;
using Xunit;

namespace CodeCoverage.Tests.Onboarding;

/// <summary>
/// #693 M12 (D7, D12, D13): with the changelog on, the onboarding pull request writes the "Type of change" template section
/// unless the template policy is off, and lists the packages that don't name their repository.
/// </summary>
public class ChangelogOnboardingTests
{
    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net11.0</TargetFramework>
            <PackageId>Acme.Tools</PackageId>
          </PropertyGroup>
        </Project>

        """;

    private static InMemoryTree Repository() => new InMemoryTree()
        .Add("src/Tools/Tools.csproj", Csproj.ReplaceLineEndings("\n"))
        .Add("src/Named/Named.csproj", Csproj.Replace("Acme.Tools", "Acme.Named").Replace("</PackageId>", "</PackageId>\n    <RepositoryUrl>https://github.com/acme/tools</RepositoryUrl>").ReplaceLineEndings("\n"))
        .Add("src/SourceLink/SourceLink.csproj", Csproj.Replace("Acme.Tools", "Acme.SourceLink").Replace("</PackageId>", "</PackageId>\n    <PublishRepositoryUrl>true</PublishRepositoryUrl>").ReplaceLineEndings("\n"))
        .Add("package.json", """{ "name": "acme", "private": true, "workspaces": ["web/*"] }""")
        .Add("web/ui/package.json", """{ "name": "@acme/ui", "version": "1.0.0", "repository": { "type": "git", "url": "https://github.com/acme/tools.git" } }""")
        .Add("web/bare/package.json", """{ "name": "@acme/bare", "version": "1.0.0" }""")
        .Add("coverage.yml", "gate:\n  patchTarget: 80\n");

    private static OnboardingInputs Inputs(bool changelog = true, bool template = true) => new()
    {
        Features = new HashSet<EOnboardingFeature> { EOnboardingFeature.Versioning },
        DefaultBranch = "main",
        Changelog = changelog,
        PrTemplate = template,
    };

    [Fact]
    public async Task With_the_changelog_on_the_template_is_created_and_unnamed_packages_are_listed()
    {
        var plan = await OnboardingPlanner.PlanAsync(Repository(), Inputs());

        plan.Files.Single(f => f.Path == ".github/pull_request_template.md").Content.Should().Be(ChangelogOnboarding.Section);
        plan.Body.Should().Contain("## Changelog");
        plan.Notes.Should().ContainSingle(n => n.StartsWith("Name the repository in these 2 packages", StringComparison.Ordinal))
            .Which.Should().Contain("`src/Tools/Tools.csproj`").And.Contain("`web/bare/package.json`")
            .And.NotContain("Named").And.NotContain("SourceLink").And.NotContain("web/ui");
    }

    [Fact]
    public async Task An_existing_template_gets_the_section_appended_once()
    {
        var tree = Repository().Add("docs/PULL_REQUEST_TEMPLATE.md", "## Summary\n\n- [ ] Tests added\n");

        var plan = await OnboardingPlanner.PlanAsync(tree, Inputs());
        var written = plan.Files.Should().ContainSingle(f => f.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Which;
        (written.Path, written.Content).Should().Be(("docs/PULL_REQUEST_TEMPLATE.md", "## Summary\n\n- [ ] Tests added\n\n" + ChangelogOnboarding.Section));

        var again = await OnboardingPlanner.PlanAsync(Repository().Add(written.Path, written.Content!), Inputs());
        again.Files.Should().NotContain(f => f.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase), "a template with the section is left as it is");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task The_template_is_untouched_with_the_changelog_or_the_template_policy_off(bool changelog, bool template)
    {
        var plan = await OnboardingPlanner.PlanAsync(Repository(), Inputs(changelog, template));

        plan.Files.Should().NotContain(f => f.Path == ChangelogOnboarding.TemplatePath);
        plan.Body.Contains("## Changelog").Should().Be(changelog);
    }

    [Fact]
    public async Task Every_box_maps_to_a_type()
    {
        var types = new List<string?>();
        foreach (var box in ChangelogOnboarding.Boxes)
            types.Add((await PrTitleBuilder.BuildAsync("Change", $"- [x] {box}", false, "acme/tools", (_, _) => Task.FromResult<IReadOnlyList<string>?>(null)))!.Type);

        types.Should().Equal("feat", "fix", "perf", "docs", "refactor", "chore");
    }
}
