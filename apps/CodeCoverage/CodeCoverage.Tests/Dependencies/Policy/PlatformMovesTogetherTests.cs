using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// prerelease-dependencies-PRD §5.3 (C1-C4, C14): what holds a platform move under P11 (platform-locations-PRD:564).
/// MintPlayer.Dotnet.Tools PR #210 waited on its rc → GA move (11.0.100-rc.1 → 11.0.100) because of two conditioned
/// <c>net11.0</c> TFMs, which that move never changes. H2: a location already right for the move holds nothing; P11 still
/// holds what would stay behind. H6/H7: every workflow moves together, so a <c>setup-*</c> input the bot can't resolve
/// holds the move too.
/// </summary>
public class PlatformMovesTogetherTests
{
    private const string RcSdk = "11.0.100-rc.1.26425.128";
    private static readonly DateTimeOffset Later = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>R7 holds a platform major for approval; C4 admits it, so the test sees P11 alone.</summary>
    private const string AdmitMajor = "dependencies:\n  autoMerge: { \"platform:dotnet\": major }\n";

    private static PlatformLocation Setup(string file, string value, string? channel = "11.0") => new() { File = file, Kind = "setup-action", Value = value, Channel = channel };

    private static PlatformLocation Tfm(string file, string value, string channel = "11.0", bool computed = false, string? reason = null)
        => new() { File = file, Kind = "tfm", Value = value, Channel = channel, Computed = computed, Reason = reason };

    /// <summary>Tools' shape: no global.json, the SDK pinned in two workflows, libraries on <c>net10.0;net11.0</c>.</summary>
    private static PlatformRecord Tools(params PlatformLocation[] more) => new()
    {
        Sdk = "dotnet",
        Source = "releases-index",
        Current = new PlatformCurrent { Channels = ["11.0"], Preview = true },
        Locations =
        [
            Setup(".github/workflows/dotnet-build-master.yml", RcSdk),
            Setup(".github/workflows/dotnet-build-any.yml", RcSdk),
            Tfm("GraphQL/MintPlayer.GraphQL.Tools/MintPlayer.GraphQL.Tools.csproj", "net10.0;net11.0"),
            .. more,
        ],
        Candidates =
        [
            new PlatformCandidate { Channel = "12.0", LatestVersion = "12.0.0", LatestSdk = "12.0.100", ReleaseTimestamp = "2026-11-10", Lts = true },
            new PlatformCandidate { Channel = "11.0", LatestVersion = "11.0.0", LatestSdk = "11.0.100", ReleaseTimestamp = "2026-11-10", Lts = false },
        ],
    };

    private static PlatformRecord InChannel(params PlatformLocation[] more) => Tools(more) with { Candidates = [Tools().Candidates[1]] };

    private static LookupDocument With(PlatformRecord platform) => Lookup() with { Platforms = [platform] };

    [Fact]
    public void C1_the_release_of_the_rc_with_literal_pins_auto_merges()
    {
        var group = Evaluate(With(InChannel()), now: Later).Groups.Should().ContainSingle().Which;

        (group.Group, group.Level, group.AutoMerge).Should().Be(("platform:dotnet 11.0", EUpdateLevel.Patch, true));
    }

    [Theory]
    [InlineData("tfm", "net11.0", "the TFM is conditioned")]
    [InlineData("tfm-property", "net11.0", "a property")]
    [InlineData("template-framework", "net11.0", "a template symbol")]
    public void C2_a_computed_tfm_on_the_channel_does_not_hold_a_move_inside_it(string kind, string value, string reason)
    {
        var conditioned = new PlatformLocation { File = "Benchmarks/Benchmarks.csproj", Kind = kind, Value = value, Channel = "11.0", Computed = true, Reason = reason };

        var outcome = Evaluate(With(InChannel(conditioned)), now: Later);

        var group = outcome.Groups.Should().ContainSingle("H2: 11.0.100-rc.1 → 11.0.100 changes no TFM").Which;
        group.AutoMerge.Should().BeTrue();
        outcome.AwaitingApproval.Should().BeEmpty();
        outcome.PlatformReports.Should().ContainSingle(r => r.File == "Benchmarks/Benchmarks.csproj", "it is still listed under \"Not moved\"");
    }

    [Fact]
    public void C3_a_property_tfm_of_unknown_channel_does_not_hold()
    {
        var property = Tfm("Directory.Build.props", "$(Tfm)", channel: null!, computed: true, reason: "a property");

        Evaluate(With(InChannel(property)), now: Later).Groups.Should().ContainSingle().Which.AutoMerge.Should().BeTrue();
    }

    [Fact]
    public void C4_a_computed_tfm_the_move_would_change_still_holds_it()
    {
        var conditioned = Tfm("Benchmarks/Benchmarks.csproj", "net11.0", computed: true, reason: "the TFM is conditioned");

        var outcome = Evaluate(With(Tools(conditioned)), Config(AdmitMajor), now: Later);

        var waiting = outcome.AwaitingApproval.Should().ContainSingle("P11: 11 → 12 would leave the conditioned net11.0 behind").Which;
        waiting.Group.Should().Be("platform:dotnet 12.0");
        waiting.Detail.Should().Contain("Benchmarks/Benchmarks.csproj").And.Contain("platforms.dotnet.exclude");
        outcome.Groups.Should().ContainSingle("#617 D36: while the channel move waits, the release inside 11.0 is offered, and H2 lets it through")
            .Which.Group.Should().Be("platform:dotnet 11.0");
    }

    [Fact]
    public void C4_a_computed_sdk_pin_on_the_channel_still_holds_a_move_inside_it()
    {
        var image = new PlatformLocation { File = "Dockerfile", Kind = "docker", Value = "mcr.microsoft.com/dotnet/sdk:${SDK}", Channel = "11.0", Computed = true, Reason = "computed value" };

        var outcome = Evaluate(With(InChannel(image)), now: Later);

        outcome.Groups.Should().BeEmpty("a version pin, unlike a TFM, changes inside the channel");
        outcome.AwaitingApproval.Should().ContainSingle().Which.Detail.Should().Contain("Dockerfile");
    }

    /// <summary>H6/H7 (C14): <c>dotnet-version: ${{ vars.DOTNET }}</c> can't be read, so the move would leave that workflow behind.</summary>
    [Fact]
    public void C14_an_unresolved_setup_input_holds_the_move_until_approved_or_excluded()
    {
        var unresolved = Setup(".github/workflows/release.yml", "${{ vars.DOTNET }}", channel: null) with { Computed = true, Reason = PlatformLocation.UnresolvedExpression };
        var lookup = With(InChannel(unresolved));

        var waiting = Evaluate(lookup, now: Later, configBlob: "b");
        waiting.Groups.Should().BeEmpty("all workflows move together (H6)");
        waiting.AwaitingApproval.Should().ContainSingle().Which.Detail.Should().Contain(".github/workflows/release.yml");

        var excluded = Evaluate(lookup, Config("dependencies:\n  platforms:\n    dotnet: { exclude: [\".github/workflows/release.yml\"] }\n"), now: Later);
        excluded.Groups.Should().ContainSingle("an excluded workflow stays behind on purpose");
    }

    /// <summary>H7: a step whose value the scan resolved (a matrix entry, an env entry, an input's default) moves through
    /// that entry; an input with no default is the caller's. Neither holds.</summary>
    [Theory]
    [InlineData("${{ matrix.dotnet }}", "resolved through matrix.dotnet")]
    [InlineData("${{ env.DOTNET }}", "resolved through env.DOTNET")]
    [InlineData("${{ inputs.dotnet-version }}", "an input the caller sets")]
    public void A_resolved_or_caller_set_setup_input_does_not_hold(string value, string reason)
    {
        var step = Setup(".github/workflows/ci.yml", value, channel: null) with { Computed = true, Reason = reason };

        Evaluate(With(InChannel(step)), now: Later).Groups.Should().ContainSingle().Which.AutoMerge.Should().BeTrue();
    }
}
