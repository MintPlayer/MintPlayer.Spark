using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Versions;

public class VersionCalculatorTests
{
    static readonly MajorAnchor Net11 = new(11, "targets net11.0");
    static readonly MajorAnchor Free = MajorAnchor.Free("targets only .NET Standard");

    static VersionDecision Next(MajorAnchor anchor, string[] existing, bool apiChanged = false, PrereleaseLabel? upstream = null, bool defaultBranch = true, int? branchMajor = null, string? lastTag = null)
    {
        var versions = existing.Select(PackageVersion.Parse).ToList();
        var last = lastTag is null ? versions.DefaultIfEmpty().Max() : PackageVersion.Parse(lastTag);
        return VersionCalculator.Next(new VersionRequest(anchor, last, versions, apiChanged, upstream, defaultBranch, branchMajor));
    }

    static string Released(VersionDecision decision)
        => decision.Should().BeOfType<VersionDecision.Release>().Which.Version.ToString();

    [Theory]
    [InlineData(false, "11.2.1")]
    [InlineData(true, "11.3.0")]
    public void The_api_hash_picks_minor_or_patch(bool apiChanged, string expected)
        => Released(Next(Net11, ["11.0.0", "11.2.0"], apiChanged)).Should().Be(expected);

    [Fact]
    public void A_new_anchor_major_starts_its_line_at_zero()
        => Released(Next(Net11, ["10.4.2"], apiChanged: false)).Should().Be("11.0.0");

    [Fact]
    public void A_new_major_in_preview_starts_a_prerelease_line()
        => Released(Next(Net11, ["10.4.2"], upstream: PrereleaseLabel.Preview)).Should().Be("11.0.0-preview.1");

    [Fact]
    public void A_prerelease_on_a_stable_line_opens_the_next_minor()
        => Released(Next(Net11, ["11.2.0"], upstream: PrereleaseLabel.Rc)).Should().Be("11.3.0-rc.1");

    [Fact]
    public void An_open_prerelease_line_continues_its_counter_whatever_the_api_did()
    {
        Released(Next(Net11, ["11.2.0", "11.3.0-preview.1", "11.3.0-preview.2"], apiChanged: true, upstream: PrereleaseLabel.Preview)).Should().Be("11.3.0-preview.3");
        Released(Next(Net11, ["11.2.0", "11.3.0-preview.2"], upstream: PrereleaseLabel.Rc)).Should().Be("11.3.0-rc.1");
    }

    [Fact]
    public void Once_upstream_is_stable_again_the_open_line_goes_stable()
        => Released(Next(Net11, ["11.2.0", "11.3.0-rc.2"])).Should().Be("11.3.0");

    [Fact]
    public void A_label_less_mature_than_the_open_line_is_refused()
        => Next(Net11, ["11.3.0-rc.1"], upstream: PrereleaseLabel.Preview).Should().BeOfType<VersionDecision.Refuse>();

    /// <summary>Anchoring the 13.x generators to .NET would compute 11; the run must say no.</summary>
    [Fact]
    public void A_major_never_goes_down()
        => Next(Net11, ["13.0.0"]).Should().BeOfType<VersionDecision.Refuse>()
            .Which.Reason.Should().Contain("from 13 to 11");

    [Fact]
    public void A_free_package_keeps_its_major()
    {
        Released(Next(Free, ["13.0.0", "13.1.0"], apiChanged: true)).Should().Be("13.2.0");
        Released(Next(Free, [])).Should().Be("1.0.0");
    }

    [Fact]
    public void A_free_package_is_not_released_from_a_release_branch()
        => Next(Free, ["13.0.0"], defaultBranch: false).Should().BeOfType<VersionDecision.Skip>();

    [Fact]
    public void A_release_branch_releases_only_its_own_major()
    {
        Released(Next(new MajorAnchor(10, "targets net10.0"), ["10.4.0", "11.0.0"], defaultBranch: false, branchMajor: 10, lastTag: "10.4.0")).Should().Be("10.4.1");
        Next(Net11, ["10.4.0"], defaultBranch: false, branchMajor: 10).Should().BeOfType<VersionDecision.Refuse>();
    }

    [Fact]
    public void An_existing_version_is_never_released_again()
    {
        // The last reachable tag is 11.2.0, but 11.2.1 was already published from elsewhere.
        var existing = new[] { "11.2.0", "11.2.1" }.Select(PackageVersion.Parse).ToList();
        var decision = VersionCalculator.Next(new VersionRequest(Net11, existing[0], existing, false, null));
        Released(decision).Should().Be("11.2.2");
    }
}
