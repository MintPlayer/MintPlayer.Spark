using CodeCoverage.Versioning.Validation;

namespace CodeCoverage.Versioning.Tests.Validation;

public class IdentifiersTests
{
    [Theory]
    [InlineData("MintPlayer.Mapper", true)]
    [InlineData("Microsoft.Extensions.DependencyInjection.Abstractions", true)]
    [InlineData("My_Package-2", true)]
    [InlineData(".Leading", false)]
    [InlineData("Trailing.", false)]
    [InlineData("two..dots", false)]
    [InlineData("with space", false)]
    [InlineData("semi;colon", false)]
    [InlineData("new\nline", false)]
    public void NuGet_ids(string id, bool valid) => Identifiers.IsNuGetId(id).Should().Be(valid);

    [Theory]
    [InlineData("@mintplayer/ng-spark", true)]
    [InlineData("lodash.merge", true)]
    [InlineData("@Scope/name", false)]
    [InlineData("Upper", false)]
    [InlineData("_private", false)]
    [InlineData(".hidden", false)]
    [InlineData("@a/b/c", false)]
    [InlineData("node_modules", false)]
    public void Npm_names(string name, bool valid) => Identifiers.IsNpmName(name).Should().Be(valid);

    [Fact]
    public void Overlong_ids_are_refused()
    {
        Identifiers.IsNuGetId(new string('a', 101)).Should().BeFalse();
        Identifiers.IsNpmName(new string('a', 215)).Should().BeFalse();
    }

    [Theory]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("sha256:0123456789abcdef", false)]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n", false)]
    public void Api_hashes(string hash, bool valid) => Identifiers.IsApiHash(hash).Should().Be(valid);

    [Theory]
    [InlineData("net8.0", true)]
    [InlineData("net8.0-windows", true)]
    [InlineData("net8.0-windows10.0.19041.0", true)]
    [InlineData("netcoreapp3.1", true)]
    [InlineData("netstandard2.0", true)]
    [InlineData("net48", true)]
    [InlineData("net8.0;net9.0", false)]
    [InlineData("$(Tfm)", false)]
    public void Target_frameworks(string tfm, bool valid) => Identifiers.IsTargetFramework(tfm).Should().Be(valid);

    [Theory]
    [InlineData("MintPlayer.Mapper/v1.0.0", true)]
    [InlineData("release/10.x", true)]
    [InlineData("a..b", false)]
    [InlineData("a//b", false)]
    [InlineData("a/.b", false)]
    [InlineData("a.lock", false)]
    [InlineData("a/", false)]
    [InlineData("a.", false)]
    [InlineData("-a", false)]
    [InlineData("a@{1}", false)]
    [InlineData("@", false)]
    [InlineData("a~1", false)]
    [InlineData("a^", false)]
    [InlineData("a:b", false)]
    [InlineData("a b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a\u0001b", false)]
    public void Ref_names_follow_check_ref_format(string name, bool valid) => Identifiers.IsRefName(name).Should().Be(valid);
}
