using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Releases;
using Xunit;

namespace CodeCoverage.Tests.PackageVersioning;

/// <summary>#570 M8c, PRD §5.5 and D7: how a backport names its source, and the <c>backported:</c> label.</summary>
public class BackportProvenanceTests
{
    [Fact]
    public void The_marker_round_trips_and_only_for_the_same_repository()
    {
        var body = "Backport of #7 to `release/dotnet-10.x`.\n\n" + BackportProvenance.Marker("acme/widgets", 7);

        body.Should().Contain("<!-- backport-of: acme/widgets#7 -->");
        BackportProvenance.FromMarker(body, "acme/widgets").Should().Be(7);
        BackportProvenance.FromMarker(body, "ACME/Widgets").Should().Be(7, "GitHub names are case-insensitive");
        BackportProvenance.FromMarker(body, "acme/other").HasValue.Should().BeFalse();
        BackportProvenance.FromMarker("no marker", "acme/widgets").HasValue.Should().BeFalse();
    }

    [Theory]
    [InlineData("Backport of #12", "fix/x", 12)]
    [InlineData("backport of #12 to release/8.x", "fix/x", 12)]
    [InlineData("This is a backport of acme/widgets#12.", "fix/x", null)]   // only a bare number in this repository
    [InlineData("Fixes #12", "fix/x", null)]
    [InlineData(null, "backport/31-dotnet-10.x", 31)]
    [InlineData(null, "backport/x-dotnet-10.x", null)]
    [InlineData("Backport of #12", "backport/31-dotnet-10.x", 12)]           // the body wins: it is what the person wrote
    public void A_hand_made_backport_names_its_source_in_the_body_or_the_head(string? body, string head, int? source)
        => BackportProvenance.FromText(body, head).Should().Be(source);

    [Fact]
    public void Cherry_pick_trailers_name_their_commits()
    {
        var sha = new string('a', 40);
        BackportProvenance.CherryPickedFrom([$"fix: guard\n\n(cherry picked from commit {sha})", "chore: tidy", $"(cherry picked from commit {sha})"])
            .Should().Equal([sha]);
        BackportProvenance.CherryPickedFrom(["(cherry picked from commit abc)"]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("backport/7-dotnet-10.x", 7, "dotnet-10.x")]
    [InlineData("backport/7-8.x", 7, "8.x")]
    [InlineData("backport/7-", null, null)]
    [InlineData("backport/-8.x", null, null)]
    [InlineData("feature/7-8.x", null, null)]
    public void A_backport_head_names_the_pull_request_and_the_line(string head, int? number, string? line)
    {
        var parsed = BackportProvenance.ParseHead(head);
        (parsed?.Number, parsed?.Line).Should().Be((number, line));
    }

    [Theory]
    [InlineData("release/dotnet-10.x", true, "backported: dotnet-10.x")]
    [InlineData("release/8.x", false, "backported: 8.x")]
    public void The_backported_label_names_the_line(string branch, bool lines, string label)
        => BackportProvenance.BackportedLabel(branch, lines ? ReleaseLinePattern.Default : null).Should().Be(label);
}
