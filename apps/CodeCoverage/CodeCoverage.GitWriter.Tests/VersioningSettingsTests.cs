using CodeCoverage.GitWriter.Endpoints;
using CodeCoverage.GitWriter.Jobs;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// The versioning job's settings as the writer re-checks them (#153 M6): the app validated them already, but the writer
/// never trusts a body, so anything the app's <c>VersioningConfig</c> would refuse is refused here too.
/// </summary>
public class VersioningSettingsTests
{
    [Fact]
    public void Absent_settings_are_the_defaults()
    {
        var settings = GitWriterVersioningRunEndpoint.Settings(null);

        settings.Should().NotBeNull();
        settings!.Ignore.Should().BeEquivalentTo(["**/*.md"]);
        settings.Groups.Should().BeEmpty();
        settings.ReleaseBranches.Should().Equal("release/*.x");
    }

    [Fact]
    public void Well_formed_settings_are_kept()
    {
        var settings = GitWriterVersioningRunEndpoint.Settings(new VersioningSettingsWire
        {
            Ignore = ["docs/**"],
            Groups = [new LockstepGroupWire { Name = "spark", Packages = ["MintPlayer.Spark*"] }],
            Anchors = new() { ["MintPlayer.Spark"] = "free" },
            ReleaseBranches = ["lts/*"],
        });

        settings.Should().NotBeNull();
        settings!.ReleaseBranches.Should().Equal("lts/*");
        settings!.Groups.Single().Name.Should().Be("spark");
        settings.Anchors["MintPlayer.Spark"].Should().Be("free");
    }

    [Fact]
    public void Prerelease_pins_survive_the_wire_from_the_app()
    {
        // The app posts its own VersioningSettings with JsonSerializerOptions.Web; the writer reads the wire type.
        var sent = new Versioning.Releases.VersioningConfig
        {
            Prerelease = "preview",
            Packages = new() { ["@mintplayer/ng-spark"] = new() { Prerelease = "upstream" }, ["Acme"] = new() { Prerelease = "rc" } },
        }.ToSettings();
        var json = System.Text.Json.JsonSerializer.Serialize(sent, System.Text.Json.JsonSerializerOptions.Web);
        json.Should().Contain("\"prerelease\":\"preview\"");

        var settings = GitWriterVersioningRunEndpoint.Settings(System.Text.Json.JsonSerializer.Deserialize<VersioningSettingsWire>(json, GitWriterHost.Json));

        settings!.Prerelease.Should().Be(Versioning.Versions.PrereleaseLabel.Preview);
        settings.PrereleasePins["@mintplayer/ng-spark"].HasValue.Should().BeFalse();
        settings.PrereleasePins["Acme"].Should().Be(Versioning.Versions.PrereleaseLabel.Rc);
    }

    [Fact]
    public void An_unknown_label_on_the_wire_is_not_read()
    {
        var read = () => System.Text.Json.JsonSerializer.Deserialize<VersioningSettingsWire>("""{"prerelease":"gamma"}""", GitWriterHost.Json);

        read.Should().Throw<System.Text.Json.JsonException>();
    }

    public static TheoryData<VersioningSettingsWire> Refused() => new()
    {
        new VersioningSettingsWire { Groups = [new LockstepGroupWire { Name = "a", Packages = ["x"] }, new LockstepGroupWire { Name = "a", Packages = ["y"] }] },
        new VersioningSettingsWire { Groups = [new LockstepGroupWire { Name = "", Packages = ["x"] }] },
        new VersioningSettingsWire { Groups = [new LockstepGroupWire { Name = "a", Packages = [] }] },
        new VersioningSettingsWire { Groups = [new LockstepGroupWire { Name = "a", Packages = ["has space"] }] },
        new VersioningSettingsWire { Anchors = new() { ["not a package id"] = "free" } },
        new VersioningSettingsWire { Anchors = new() { ["Acme"] = "bad\nvalue" } },
        new VersioningSettingsWire { Ignore = [.. Enumerable.Repeat("**/*.md", 65)] },
        new VersioningSettingsWire { ReleaseBranches = ["release/../main"] },
        new VersioningSettingsWire { ReleaseBranches = ["release/*.x; rm"] },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Malformed_settings_are_refused(VersioningSettingsWire wire)
        => GitWriterVersioningRunEndpoint.Settings(wire).Should().BeNull();
}
