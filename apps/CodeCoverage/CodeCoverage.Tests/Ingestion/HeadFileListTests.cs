using CodeCoverage.Ingestion;
using MintPlayer.Assertions;
using Xunit;

namespace CodeCoverage.Tests.Ingestion;

public class HeadFileListTests
{
    [Fact]
    public void V1_paths_only_yields_no_oids()
    {
        var list = HeadFileList.Parse("src/a.cs\nsrc\\b.cs\n\n");

        list.HasOids.Should().BeFalse();
        list.Paths.Should().Equal(["src/a.cs", "src/b.cs"]);
        list.OidFor("src/a.cs").Should().BeNull();
    }

    [Fact]
    public void V2_oid_prefixed_lines_yield_oids_per_unified_path()
    {
        const string oidA = "0123456789abcdef0123456789abcdef01234567";
        const string oidB = "89ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var list = HeadFileList.Parse($"{oidA} src/a.cs\n{oidB}  src\\b.cs\n");

        list.HasOids.Should().BeTrue();
        list.Paths.Should().Equal(["src/a.cs", "src/b.cs"]);
        list.OidFor("src/a.cs").Should().Be(oidA);
        list.OidFor("src/b.cs").Should().Be(oidB.ToLowerInvariant());
        list.OidFor("src/c.cs").Should().BeNull();
    }

    [Fact]
    public void V2_accepts_sha256_oids()
    {
        var oid = new string('a', 64);
        var list = HeadFileList.Parse($"{oid} a.txt");

        list.HasOids.Should().BeTrue();
        list.OidFor("a.txt").Should().Be(oid);
    }

    [Fact]
    public void V2_skips_lines_without_an_oid_instead_of_treating_them_as_paths()
    {
        var oid = new string('b', 40);
        var list = HeadFileList.Parse($"{oid} a.txt\nnot-an-oid-line\n");

        list.Paths.Should().Equal(["a.txt"]);
    }

    [Fact]
    public void Duplicate_paths_keep_the_first_oid()
    {
        var first = new string('1', 40);
        var second = new string('2', 40);
        var list = HeadFileList.Parse($"{first} a.txt\n{second} a.txt\n");

        list.Count.Should().Be(1);
        list.OidFor("a.txt").Should().Be(first);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n\n")]
    public void Empty_content_is_the_empty_list(string? content)
    {
        var list = HeadFileList.Parse(content);

        list.Count.Should().Be(0);
        list.HasOids.Should().BeFalse();
    }
}
