using CodeCoverage.Forge;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>Segment P: the <c>/release</c> comment (P2, P4, P5).</summary>
public class ReleaseCommandTests
{
    [Fact]
    public void A_bare_release_moves_the_changed_lines_to_the_newest_green_commit()
    {
        var request = ReleaseCommand.Parse("/release\nthanks");

        request.Should().NotBeNull();
        request!.Lines.Should().BeEmpty();
        request.All.Should().BeFalse();
        request.Sha.Should().BeNull();
        request.Versions.Should().BeEmpty();
    }

    [Fact]
    public void Lines_a_sha_and_versions_are_told_apart()
    {
        var request = ReleaseCommand.Parse("/release dotnet-11.x, release/angular-22.x `C627A9197` MintPlayer.Spark@11.2.0 @mintplayer/ng-spark@22.54.0-preview.1");

        request.Should().NotBeNull();
        request!.Lines.Should().Equal("dotnet-11.x", "release/angular-22.x");
        request.Sha.Should().Be("c627a9197");
        request.Versions.Should().Equal(new ReleaseVersionRequest("MintPlayer.Spark", "11.2.0"), new ReleaseVersionRequest("@mintplayer/ng-spark", "22.54.0-preview.1"));
    }

    [Fact]
    public void All_names_every_promote_line()
        => ReleaseCommand.Parse("/RELEASE all")!.All.Should().BeTrue();

    [Theory]
    [InlineData("please /release")]
    [InlineData("/released")]
    [InlineData("/release all dotnet-11.x")]
    [InlineData("/release all all")]
    [InlineData("/release c627a9197 5b71146ae")]
    [InlineData("/release --force")]
    [InlineData("/release dotnet-11.x $(id)")]
    [InlineData("/release Pkg@1.2")]
    [InlineData("/release Pkg@01.2.3")]
    [InlineData("/release Pkg@1.2.3 Pkg@1.2.4")]
    [InlineData("/release @scope@1.2.3")]
    [InlineData("/release @/pkg@1.2.3")]
    [InlineData("/release Pkg@1.2.3-")]
    public void Anything_it_cannot_read_is_no_command(string body)
        => ReleaseCommand.Parse(body).Should().BeNull();

    [Fact]
    public void Too_many_lines_is_no_command()
        => ReleaseCommand.Parse("/release " + string.Join(' ', Enumerable.Range(1, 11).Select(i => $"dotnet-{i}.x"))).Should().BeNull();
}
