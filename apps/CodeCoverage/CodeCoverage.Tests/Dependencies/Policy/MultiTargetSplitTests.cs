using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// multi-target-dependency-split-PRD P2/P3 (D-b, D-c, D-d, M4-M6, M12, M13): in a project that targets two or more .NET
/// channels, a platform package moves per TFM, in the platform group, as one item with a layout; a platform move adds the
/// new channel's TFM and drops end-of-life ones.
/// </summary>
public class MultiTargetSplitTests
{
    private static readonly DateTimeOffset Later = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Hasher = "FolderHasher/FolderHasher.csproj";
    private const string Targets = "Targets/Targets.csproj";
    private const string Fsg = "Microsoft.Extensions.FileSystemGlobbing";

    private const string AdmitMajor = "dependencies:\n  autoMerge: { \"platform:dotnet\": major }\n";

    private static PlatformLocation Tfm(string file, string value) => new()
    {
        File = file,
        Kind = "tfm",
        Value = value,
        Channel = value.Split(';').Select(t => System.Text.RegularExpressions.Regex.Match(t, @"^net(\d+\.\d+)$").Groups[1].Value)
            .Where(c => c.Length > 0).MaxBy(c => int.Parse(c.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture)),
    };

    private static PlatformRecord Dotnet(IEnumerable<PlatformLocation> tfms, string[]? endOfLife = null, params PlatformCandidate[] candidates) => new()
    {
        Sdk = "dotnet",
        Source = "releases-index",
        Current = new PlatformCurrent { Channels = ["11.0", "10.0"], EndOfLife = endOfLife is null ? null : [.. endOfLife] },
        Locations = [new PlatformLocation { File = "global.json", Kind = "global.json", Value = "11.0.100", Channel = "11.0" }, .. tfms],
        Candidates = [.. candidates],
    };

    private static PlatformCandidate Row(string channel, string version, string sdk)
        => new() { Channel = channel, LatestVersion = version, LatestSdk = sdk, ReleaseTimestamp = "2026-11-10" };

    private static LookupRecord Nuget(string file, string current, string? tfm, params string[] versions)
        => Record(Fsg, current, [.. versions.Select(v => Candidate(v.StartsWith(current.Split('.')[0] + ".", StringComparison.Ordinal) ? EUpdateType.Patch : EUpdateType.Major, v))])
            with
            {
                Ecosystem = "nuget",
                Manager = "nuget",
                Datasource = "nuget",
                PackageFile = file,
                LockFiles = [],
                Condition = tfm is null ? null : $"'$(TargetFramework)' == '{tfm}'",
                TargetFrameworks = tfm is null ? null : [tfm],
            };

    private static DependencyPolicyOutcome Run(PlatformRecord platform, string? yml, params LookupRecord[] records)
        => Evaluate(Lookup(records) with { Platforms = [platform] }, Config(yml), now: Later);

    private static string Layout(DependencyPlannedItem item)
        => string.Join(", ", item.Layout!.Select(l => $"{l.TargetFramework}={l.Version}"));

    /// <summary>
    /// Tools #216: FolderHasher (net10.0;net11.0) had an unconditioned Microsoft.Extensions.FileSystemGlobbing 10.0.5, the
    /// net11.0-only Targets the same. The patch to 10.0.15 went in an ordinary group, the major to 11.0.0 in the platform
    /// group applied last, and restore saw a downgrade (NU1605). Now both move in the one platform group: FolderHasher per
    /// TFM, Targets to 11.0.0 (H1), and no ordinary group touches the package.
    /// </summary>
    [Fact]
    public void Tools_216_replica_moves_the_package_in_one_platform_group_per_TFM()
    {
        var outcome = Run(Dotnet([Tfm(Hasher, "net10.0;net11.0"), Tfm(Targets, "net11.0")]), null,
            Nuget(Hasher, "10.0.5", null, "10.0.15", "11.0.0"),
            Nuget(Targets, "10.0.5", null, "10.0.15", "11.0.0"));

        outcome.Groups.Should().ContainSingle();
        var group = outcome.Groups[0];
        group.Group.Should().Be("platform:dotnet 11.0");
        group.Items.Should().HaveCount(2);
        var hasher = group.Items.Single(i => i.Record.PackageFile == Hasher);
        Layout(hasher).Should().Be("net10.0=10.0.15, net11.0=11.0.0");
        var item = hasher.ToPlanItem();
        (item.NewVersion, item.CurrentValue, item.Layout!.Count).Should().Be(("11.0.0", "10.0.5", 2));
        var targets = group.Items.Single(i => i.Record.PackageFile == Targets);
        targets.Layout.Should().BeNull();
        targets.Candidate.Version.Should().Be("11.0.0");
    }

    [Fact]
    public void An_already_split_project_takes_each_TFMs_patch_in_one_item()
    {
        var outcome = Run(Dotnet([Tfm(Hasher, "net10.0;net11.0")]), null,
            Nuget(Hasher, "10.0.5", "net10.0", "10.0.15", "11.0.1"),
            Nuget(Hasher, "11.0.0", "net11.0", "11.0.1"));

        Planned(outcome).Should().Equal("platform:dotnet 11.0: Microsoft.Extensions.FileSystemGlobbing@11.0.1");
        Layout(outcome.Groups[0].Items.Single()).Should().Be("net10.0=10.0.15, net11.0=11.0.1");
    }

    [Fact]
    public void A_split_project_with_nothing_new_plans_nothing()
    {
        var outcome = Run(Dotnet([Tfm(Hasher, "net10.0;net11.0")]), null,
            Nuget(Hasher, "10.0.15", "net10.0", "11.0.0"),
            Nuget(Hasher, "11.0.0", "net11.0", "10.0.15"));

        outcome.Groups.Should().BeEmpty();
    }

    /// <summary>P6 (S7, M11): with central package management the version lives in Directory.Packages.props, which builds for
    /// every TFM list under its folder; its unconditioned PackageVersion splits into one conditioned PackageVersion per TFM.</summary>
    [Fact]
    public void P6_a_central_PackageVersion_splits_over_the_TFMs_under_its_folder()
    {
        const string props = "Directory.Packages.props";
        var outcome = Run(Dotnet([Tfm(Hasher, "net10.0;net11.0"), Tfm(Targets, "net11.0")]), null,
            Nuget(props, "10.0.5", null, "10.0.15", "11.0.0"));

        var item = outcome.Groups.Single(g => g.Group == "platform:dotnet 11.0").Items.Single();
        item.Record.PackageFile.Should().Be(props);
        Layout(item).Should().Be("net10.0=10.0.15, net11.0=11.0.0");
    }

    [Fact]
    public void A_TFM_without_a_channel_takes_the_lowest_channels_version()
    {
        var outcome = Run(Dotnet([Tfm(Hasher, "netstandard2.0;net10.0;net11.0")]), null,
            Nuget(Hasher, "10.0.5", null, "10.0.15", "11.0.0"));

        Layout(outcome.Groups.Single().Items.Single()).Should().Be("netstandard2.0=10.0.15, net10.0=10.0.15, net11.0=11.0.0");
    }

    /// <summary>D-f: a condition the lookup could not map (no targetFrameworks) or one the bot does not write is never split;
    /// the package stays ordinary and the reason is listed.</summary>
    [Fact]
    public void An_unmapped_condition_is_not_split_and_is_reported()
    {
        var odd = Nuget(Hasher, "10.0.5", null, "10.0.15", "11.0.0") with { Condition = "'$(Configuration)' == 'Debug'" };

        var outcome = Run(Dotnet([Tfm(Hasher, "net10.0;net11.0")]), null, odd);

        outcome.Groups.Should().NotContain(g => g.Items.Any(i => i.Layout != null));
        outcome.Ignored.Concat(outcome.Held).Concat(outcome.Available).Should()
            .Contain(e => e.DepName == Fsg && e.Detail.Contains("multi-target", StringComparison.Ordinal));
    }

    /// <summary>P3, M13: a major move adds the new channel's TFM to a multi-target project, whose new TFM takes the new major.</summary>
    [Fact]
    public void A_platform_move_adds_the_new_TFM_and_its_version()
    {
        var outcome = Run(Dotnet([Tfm(Hasher, "net10.0;net11.0")], null, Row("12.0", "12.0.0", "12.0.100")),
            AdmitMajor,
            Nuget(Hasher, "10.0.15", "net10.0", "11.0.1", "12.0.0"),
            Nuget(Hasher, "11.0.0", "net11.0", "11.0.1", "12.0.0"));

        var group = outcome.Groups.Should().ContainSingle(g => g.Platform != null).Which;
        group.Group.Should().Be("platform:dotnet 12.0");
        group.Platform!.TargetFrameworks.Should().ContainSingle().Which.Should().Be(new PlanTargetFrameworks { File = Hasher, Value = "net10.0;net11.0;net12.0" });
        Layout(group.Items.Single()).Should().Be("net10.0=10.0.15, net11.0=11.0.1, net12.0=12.0.0");
    }

    /// <summary>M6: an end-of-life channel's TFM leaves with its ItemGroup; keepPrevious keeps a minimum of older channels.</summary>
    [Theory]
    [InlineData(0, "net11.0;net12.0", "net11.0=11.0.1, net12.0=12.0.0")]
    [InlineData(2, "net10.0;net11.0;net12.0", "net10.0=10.0.15, net11.0=11.0.1, net12.0=12.0.0")]
    public void An_end_of_life_TFM_leaves_unless_keepPrevious_keeps_it(int keepPrevious, string tfms, string layout)
    {
        var yml = $"{AdmitMajor}  platforms:\n    dotnet:\n      keepPrevious: {keepPrevious}\n";
        var outcome = Run(Dotnet([Tfm(Hasher, "net10.0;net11.0")], ["10.0"], Row("12.0", "12.0.0", "12.0.100")), yml,
            Nuget(Hasher, "10.0.15", "net10.0", "11.0.1", "12.0.0"),
            Nuget(Hasher, "11.0.0", "net11.0", "11.0.1", "12.0.0"));

        var group = outcome.Groups.SingleOrDefault(g => g.Platform != null)
            ?? throw new Xunit.Sdk.XunitException(string.Join("; ", outcome.AwaitingApproval.Select(e => e.Detail)));
        group.Platform!.TargetFrameworks!.Single().Value.Should().Be(tfms);
        Layout(group.Items.Single()).Should().Be(layout);
    }

    /// <summary>#606 D3 for a split: the new TFM needs the package's release on the new major, else the move waits.</summary>
    [Fact]
    public void A_new_TFM_without_a_release_on_its_major_holds_the_move()
    {
        var outcome = Run(Dotnet([Tfm(Hasher, "net10.0;net11.0")], null, Row("12.0", "12.0.0", "12.0.100")), AdmitMajor,
            Nuget(Hasher, "10.0.15", "net10.0", "11.0.1"),
            Nuget(Hasher, "11.0.0", "net11.0", "11.0.1"));

        outcome.Groups.Should().NotContain(g => g.Group == "platform:dotnet 12.0");
        outcome.Available.Concat(outcome.Held).Concat(outcome.AwaitingApproval)
            .Should().Contain(e => e.Reason == EDependencyReportReason.Pending && e.Detail.Contains(Fsg, StringComparison.Ordinal));
    }
}
