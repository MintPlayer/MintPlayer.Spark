using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Git.Dependencies;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// S3 on records: the policy engine replayed on THIS repository's real lookup (Renovate 44.125.2, captured
/// 2026-10-15T19:17Z at ee304c5c, 504 dependencies, 69 with updates; <c>Fixtures/Dependencies/renovate/mintplayer-spark</c>),
/// with this repository's real <c>coverage.yml</c> (no <c>dependencies:</c> section, so the built-in defaults: threshold
/// minor, <c>@angular/*</c> never, cooldown three days).
/// </summary>
/// <remarks>
/// The golden plans below were derived by hand from the report's timestamps and labels (PRD §13.12 D-P17), not by running
/// the engine: they are the independent half of the check. When the fixture is re-captured, derive them again.
/// </remarks>
public class SelectorReplayTests
{
    private static readonly DateTimeOffset Captured = new(2026, 10, 15, 19, 17, 0, TimeSpan.Zero);

    private static LookupDocument RealLookup() => RenovateReportMapper.Document(RenovateReport("mintplayer-spark"), Sha);

    private static DependencyPolicyConfig RealConfig()
    {
        var parsed = DependenciesYml.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "coverage.yml")));
        parsed.Errors.Should().BeEmpty();
        return new DependencyPolicyConfig { File = parsed.Section };
    }

    private static readonly string[] Angular =
    [
        "@angular/animations", "@angular/build", "@angular/cdk", "@angular/cli", "@angular/common", "@angular/compiler",
        "@angular/compiler-cli", "@angular/core", "@angular/forms", "@angular/platform-browser", "@angular/router",
        "@angular/service-worker",
    ];

    [Fact]
    public void At_capture_time_only_what_is_three_days_old_ships()
    {
        var outcome = Evaluate(RealLookup(), RealConfig(), now: Captured);

        Planned(outcome).Should().Equal(
        [
            // Patch first: the lockfile-only @types/node bump (2026-10-09) and JsonSchema.Net 9.4.1 (2026-10-11).
            "@types/node: @types/node@^24.19.1",
            "JsonSchema.Net: JsonSchema.Net@9.4.1",
            // Then minor: Microsoft.CodeAnalysis 5.9.0 in its three projects, postfix 4.4.0 under its own major, RavenDB 7.2.
            "Microsoft.CodeAnalysis: Microsoft.CodeAnalysis@5.9.0",
            "boky/postfix: boky/postfix@4.4.0",
            "docker.io/ravendb/ravendb: docker.io/ravendb/ravendb@7.2.3-ubuntu.22.04-x64",
        ]);
        outcome.Groups.Single(g => g.Group == "Microsoft.CodeAnalysis").Items.Should().HaveCount(3);
        outcome.Groups.Should().OnlyContain(g => g.AutoMerge);

        // R6: the majors and every @angular/* update wait for an approval; an Angular patch is "never" by default.
        outcome.AwaitingApproval.Select(e => e.Group).Distinct().Order(StringComparer.Ordinal).Should()
            .Equal([.. Angular.Append("@types/node").Append("boky/postfix").Append("typescript").Order(StringComparer.Ordinal)]);
        outcome.AwaitingApproval.Where(e => e.Group == "typescript").Select(e => e.Target).Should().BeEquivalentTo(["7.0.2", "7.0.2"]);

        // D-S12a: playwright has no releaseTimestamp, so its clock starts now.
        outcome.Available.Should().ContainSingle(e => e.DepName == "mcr.microsoft.com/playwright").Which.Until.Should().Be(Captured.AddDays(3));
        // The .NET 11 rc.2 wave of 2026-10-13 is still inside its cooldown.
        outcome.Available.Should().Contain(e => e.DepName == "Microsoft.NET.Test.Sdk" && e.Reason == EDependencyReportReason.Cooldown);
        outcome.Available.Should().Contain(e => e.DepName == "dotnet-sdk" && e.Reason == EDependencyReportReason.Cooldown);

        DependencyContractReader.WritePlan(outcome.ToPlan("replay-a", Sha));
    }

    [Fact]
    public void A_week_later_everything_under_the_threshold_ships_and_the_first_seen_clock_has_run()
    {
        var first = Evaluate(RealLookup(), RealConfig(), now: Captured);

        var outcome = Evaluate(RealLookup(), RealConfig(), state: first.NextState, now: Captured.AddDays(7));

        outcome.Groups.Select(g => g.Group).Should().Equal(
        [
            "@analogjs/vite-plugin-angular", "@analogjs/vitest-angular", "@angular-devkit/build-angular", "@types/node",
            "@vitest/coverage-v8", "AngleSharp", "JsonSchema.Net", "Microsoft.AspNetCore.Authentication.Certificate",
            "Microsoft.AspNetCore.Authentication.Facebook", "Microsoft.AspNetCore.Authentication.Google",
            "Microsoft.AspNetCore.Authentication.JwtBearer", "Microsoft.AspNetCore.Authentication.MicrosoftAccount",
            "Microsoft.AspNetCore.Authentication.OpenIdConnect", "Microsoft.AspNetCore.Mvc.Testing", "Microsoft.AspNetCore.TestHost",
            "Octokit.Webhooks.AspNetCore", "QRCoder", "cytoscape", "dotnet-sdk", "jsdom", "vitest",
            "Microsoft.CodeAnalysis", "Microsoft.NET.Test.Sdk", "boky/postfix", "curlimages/curl", "docker.io/ravendb/ravendb",
            "mcr.microsoft.com/playwright",
        ]);
        outcome.Groups.Single(g => g.Group == "@vitest/coverage-v8").Items.Select(i => i.ToPlanItem().NewValue)
            .Should().BeEquivalentTo(["^5.0.3", "5.0.5"], because: "the action's range keeps its value (lockfile-only), the root pin moves");
        outcome.Groups.Single(g => g.Group == "Microsoft.AspNetCore.Mvc.Testing").Items.Should().HaveCount(9);
        outcome.Groups.Single(g => g.Group == "dotnet-sdk").Items.Should().HaveCount(3);
        outcome.AwaitingApproval.Select(e => e.Group).Distinct().Should().HaveCount(15);
        outcome.Groups.Where(g => g.Group.StartsWith("@angular/", StringComparison.Ordinal)).Should().BeEmpty();

        DependencyContractReader.WritePlan(outcome.ToPlan("replay-b", Sha));
    }

    [Fact]
    public void Every_dependency_with_an_update_is_planned_or_listed_never_dropped()
    {
        var lookup = RealLookup();

        var outcome = Evaluate(lookup, RealConfig(), now: Captured);

        var accounted = outcome.Groups.SelectMany(g => g.Items).Select(i => (i.Record.DepName, i.Record.PackageFile))
            .Concat(outcome.Available.Concat(outcome.Held).Concat(outcome.AwaitingApproval).Concat(outcome.Ignored).Select(e => (e.DepName, e.PackageFile)))
            .ToHashSet();
        lookup.Records.Where(r => r.Candidates.Count > 0).Should().OnlyContain(r => accounted.Contains((r.DepName, r.PackageFile)));
    }

    [Fact]
    public void Approving_typescript_brings_the_major_in_without_auto_merge()
    {
        var first = Evaluate(RealLookup(), RealConfig(), now: Captured, configBlob: "blob-1");
        var state = first.NextState;
        DependencyHolds.Approve(state, first, "typescript", "blob-1", "PieterjanDeClippel", 42, Captured).Should().NotBeNull();

        var outcome = Evaluate(RealLookup(), RealConfig(), state: state, now: Captured.AddHours(1), configBlob: "blob-1");

        var typescript = outcome.Groups.Should().ContainSingle(g => g.Group == "typescript").Which;
        typescript.Approved.Should().BeTrue();
        typescript.AutoMerge.Should().BeFalse();
        typescript.Items.Select(i => i.ToPlanItem().NewValue).Should().BeEquivalentTo(["^7.0.0", "7.0.2"]);
        outcome.AutoMergeable.Should().BeFalse();
    }
}
