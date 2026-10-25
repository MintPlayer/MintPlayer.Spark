using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Versions;

public class VersionTagTests
{
    const string Hash = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("MintPlayer.Mapper/v13.2.0", "MintPlayer.Mapper", "13.2.0")]
    [InlineData("@mintplayer/ng-spark/v22.4.0", "@mintplayer/ng-spark", "22.4.0")]
    [InlineData("@vendor/vue-thing/v3.0.0-rc.2", "@vendor/vue-thing", "3.0.0-rc.2")]
    public void A_tag_name_splits_at_the_last_slash_v(string name, string id, string version)
    {
        VersionTag.TryParse(name, out var tag).Should().BeTrue();
        tag.PackageId.Should().Be(id);
        tag.Version.ToString().Should().Be(version);
        tag.Name.Should().Be(name);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("MintPlayer.Mapper/v13.2")]
    [InlineData("MintPlayer.Mapper/13.2.0")]
    [InlineData("Bad Id/v1.0.0")]
    [InlineData("@Scope/Upper/v1.0.0")]
    [InlineData("x..y/v1.0.0")]
    [InlineData("pkg/v1.0.0.lock")]
    public void Other_tags_are_not_version_tags(string name)
        => VersionTag.TryParse(name, out _).Should().BeFalse();

    [Fact]
    public void An_id_that_cannot_be_a_ref_is_refused()
    {
        var act = () => VersionTag.For("a b", PackageVersion.Parse("1.0.0"));
        act.Should().Throw<VersioningException>();
    }

    [Fact]
    public void The_annotation_carries_the_hash_and_the_engine_and_reads_back()
    {
        var tag = VersionTag.For("MintPlayer.Mapper", PackageVersion.Parse("13.2.0"));
        var message = new TagAnnotation(Hash, "1.4.0").Format(tag);

        message.Should().Be($"MintPlayer.Mapper 13.2.0\n\napi-hash: {Hash}\nengine: CodeCoverage.Versioning 1.4.0\n");
        TagAnnotation.Parse(message).Should().Be(new TagAnnotation(Hash, "1.4.0"));
    }

    [Theory]
    [InlineData("Release 13.2.0")]
    [InlineData("api-hash: sha256:XYZ")]
    [InlineData("api-hash: sha1:0123456789abcdef0123456789abcdef01234567")]
    [InlineData(null)]
    public void A_missing_or_malformed_hash_reads_as_none(string? message)
        => TagAnnotation.Parse(message).ApiHash.Should().BeNull();

    [Fact]
    public void A_malformed_hash_is_never_written()
    {
        var act = () => new TagAnnotation("sha256:nope", null).Format(VersionTag.For("pkg", PackageVersion.Parse("1.0.0")));
        act.Should().Throw<VersioningException>();
    }

    private const string Run = "https://github.com/MintPlayer/MintPlayer.Spark/actions/runs/18234567890";

    [Fact]
    public void The_annotation_names_the_run_that_wrote_it_and_reads_it_back()
    {
        var tag = VersionTag.For("MintPlayer.Mapper", PackageVersion.Parse("13.2.0"));
        var message = new TagAnnotation(Hash, "1.4.0", Run).Format(tag);

        message.Should().Be($"MintPlayer.Mapper 13.2.0\n\napi-hash: {Hash}\nengine: CodeCoverage.Versioning 1.4.0\nrun: {Run}\n");
        TagAnnotation.Parse(message).Should().Be(new TagAnnotation(Hash, "1.4.0", Run));
        // A seed tag (or one from before #544) has no run.
        TagAnnotation.Parse($"MintPlayer.Mapper 13.2.0\n\napi-hash: {Hash}\n").Run.Should().BeNull();
    }

    [Fact]
    public void The_run_url_comes_from_the_runner_environment()
    {
        TagAnnotation.RunUrl("https://github.com/", "MintPlayer/MintPlayer.Spark", "18234567890").Should().Be(Run);
        TagAnnotation.RunUrl("https://github.com", "MintPlayer/MintPlayer.Spark", null).Should().BeNull();
        TagAnnotation.RunUrl(null, "MintPlayer/MintPlayer.Spark", "1").Should().BeNull();
        TagAnnotation.RunUrl("https://github.com", "MintPlayer/../evil", "1").Should().BeNull();
    }

    [Theory]
    [InlineData("run: http://github.com/a/b/actions/runs/1")]
    [InlineData("run: https://github.com/a/b/actions/runs/1x")]
    [InlineData("run: https://github.com/a/b/actions/runs/1\" onclick=\"x")]
    [InlineData("run: javascript:alert(1)")]
    public void A_malformed_run_reads_as_none(string message)
        => TagAnnotation.Parse(message).Run.Should().BeNull();

    [Fact]
    public void A_malformed_run_is_never_written()
    {
        var act = () => new TagAnnotation(null, null, "javascript:alert(1)").Format(VersionTag.For("pkg", PackageVersion.Parse("1.0.0")));
        act.Should().Throw<VersioningException>();
    }
}
