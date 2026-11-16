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

        settings!.Prerelease.Should().Be(Versioning.Versions.PrereleasePin.Of(Versioning.Versions.PrereleaseLabel.Preview));
        settings.PrereleasePins["@mintplayer/ng-spark"].Should().Be(Versioning.Versions.PrereleasePin.Upstream);
        settings.PrereleasePins["Acme"].Should().Be(Versioning.Versions.PrereleasePin.Of(Versioning.Versions.PrereleaseLabel.Rc));
    }

    [Fact]
    public void Stable_pins_survive_the_wire_from_the_app()
    {
        // versioning-beta-upstream-PRD G2: "stable" at every level, and distinct from "upstream" (both carry no label).
        var sent = new Versioning.Releases.VersioningConfig
        {
            Prerelease = "stable",
            Packages = new() { ["MintPlayer.GraphQL"] = new() { Prerelease = "stable" }, ["Acme"] = new() { Prerelease = "upstream" } },
            Lines = new() { Prerelease = new() { ["dotnet-10.x"] = "stable" } },
        }.ToSettings();
        var json = System.Text.Json.JsonSerializer.Serialize(sent, System.Text.Json.JsonSerializerOptions.Web);

        var settings = GitWriterVersioningRunEndpoint.Settings(System.Text.Json.JsonSerializer.Deserialize<VersioningSettingsWire>(json, GitWriterHost.Json));

        settings!.Prerelease.Should().Be(Versioning.Versions.PrereleasePin.Stable);
        settings.PrereleasePins["MintPlayer.GraphQL"].Should().Be(Versioning.Versions.PrereleasePin.Stable);
        settings.PrereleasePins["Acme"].Should().Be(Versioning.Versions.PrereleasePin.Upstream);
        settings.LinePrerelease["dotnet-10.x"].Should().Be(Versioning.Versions.PrereleasePin.Stable);
    }

    [Fact]
    public void Release_lines_survive_the_wire_from_the_app()
    {
        // #568 D2, D19: the pattern and the line pins reach the writer, which reads each run's line from the branch itself.
        var sent = new Versioning.Releases.VersioningConfig
        {
            Lines = new() { Pattern = "support/{ecosystem}/{major}", Prerelease = new() { ["dotnet-11.x"] = "upstream", ["angular-23.x"] = "next" } },
            Packages = new() { ["Acme.Generators"] = new() { Line = "dotnet" } },
        }.ToSettings();
        var json = System.Text.Json.JsonSerializer.Serialize(sent, System.Text.Json.JsonSerializerOptions.Web);

        var settings = GitWriterVersioningRunEndpoint.Settings(System.Text.Json.JsonSerializer.Deserialize<VersioningSettingsWire>(json, GitWriterHost.Json));

        settings!.Lines!.Text.Should().Be("support/{ecosystem}/{major}");
        settings.LinePins["Acme.Generators"].Should().Be("dotnet");
        settings.LinePrerelease["dotnet-11.x"].Should().Be(Versioning.Versions.PrereleasePin.Upstream);
        settings.LinePrerelease["angular-23.x"].Should().Be(Versioning.Versions.PrereleasePin.Of(Versioning.Versions.PrereleaseLabel.Next));
        GitWriterVersioningRunEndpoint.LineOf(settings, "support/dotnet/10", "main", out var line).Should().BeTrue();
        line.Should().Be(new Versioning.Releases.ReleaseLine("dotnet", 10));
        GitWriterVersioningRunEndpoint.LineOf(settings, "main", "main", out line).Should().BeTrue();
        line.Should().BeNull();
        GitWriterVersioningRunEndpoint.LineOf(settings, "release/10.x", "main", out _).Should().BeFalse();
    }

    [Fact]
    public void Without_lines_every_branch_passes_to_the_old_release_branch_check()
        => GitWriterVersioningRunEndpoint.LineOf(new Versioning.Releases.VersioningSettings(), "release/10.x", "main", out _).Should().BeTrue();

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
        new VersioningSettingsWire { Lines = new ReleaseLinesWire { Text = "release/{major}.x" } },
        new VersioningSettingsWire { Lines = new ReleaseLinesWire() },
        new VersioningSettingsWire { LinePins = new() { ["Acme"] = "Dot Net" } },
        new VersioningSettingsWire { LinePrerelease = new() { ["release/dotnet-11.x"] = Versioning.Versions.PrereleasePin.Upstream } },
        new VersioningSettingsWire { LinePrerelease = new() { ["dotnet-011.x"] = Versioning.Versions.PrereleasePin.Upstream } },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Malformed_settings_are_refused(VersioningSettingsWire wire)
        => GitWriterVersioningRunEndpoint.Settings(wire).Should().BeNull();
}
