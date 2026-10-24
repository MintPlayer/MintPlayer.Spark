using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Versions;

public class MajorAnchorTests
{
    [Theory]
    [InlineData(10, "net10.0")]
    [InlineData(11, "net8.0", "net11.0", "netstandard2.0")]
    [InlineData(8, "net8.0-windows")]
    [InlineData(9, "net9.0-windows10.0.19041.0", "net48")]
    [InlineData(3, "netcoreapp3.1")]
    [InlineData(5, "netcoreapp3.1", "net5.0")]
    public void A_dotnet_project_takes_its_highest_target(int major, params string[] tfms)
        => MajorAnchor.FromTargetFrameworks(tfms).Major.Should().Be(major);

    [Theory]
    [InlineData("netstandard2.0")]
    [InlineData("netstandard2.1", "net48", "net472")]
    public void Netstandard_and_framework_only_projects_are_free(params string[] tfms)
        => MajorAnchor.FromTargetFrameworks(tfms).IsFree.Should().BeTrue();

    [Fact]
    public void An_unknown_target_framework_is_refused()
    {
        var act = () => MajorAnchor.FromTargetFrameworks(["net8.0", "$(Evil)"]);
        act.Should().Throw<VersioningException>();
    }

    [Fact]
    public void A_frontend_library_takes_the_resolved_framework_major()
    {
        MajorAnchor.FromFramework("@angular/core", "22.3.1", "^22.0.0").Major.Should().Be(22);
        MajorAnchor.FromFramework("react", "19.1.0", null).Major.Should().Be(19);
        MajorAnchor.FromFramework("@angular/core", "23.0.0-next.4", "^22.0.0 || ^23.0.0-next.0").Major.Should().Be(23);
    }

    [Fact]
    public void The_coverage_yml_override_wins()
    {
        MajorAnchor.FromOverride("free", null).IsFree.Should().BeTrue();
        MajorAnchor.FromOverride("Microsoft.CodeAnalysis.CSharp", "4.14.0").Major.Should().Be(4);
        ((Action)(() => MajorAnchor.FromOverride("Microsoft.CodeAnalysis.CSharp", null))).Should().Throw<VersioningException>();
        ((Action)(() => MajorAnchor.FromOverride("not a package", "1.0.0"))).Should().Throw<VersioningException>();
    }

    [Theory]
    [InlineData("23.0.0", "^22.0.0")]
    [InlineData("23.0.0-next.4", "^23.0.0")]
    [InlineData("22.0.0", "not a range")]
    [InlineData("twenty-two", "^22.0.0")]
    public void A_peer_range_that_excludes_the_resolved_framework_is_refused(string resolved, string peer)
    {
        var act = () => MajorAnchor.FromFramework("@angular/core", resolved, peer);
        act.Should().Throw<VersioningException>();
    }
}
