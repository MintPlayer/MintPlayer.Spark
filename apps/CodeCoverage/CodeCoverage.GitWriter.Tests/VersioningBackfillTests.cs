using System.Net.Http.Json;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;
using CodeCoverage.Versioning.Releases;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #638 D5-D8: <c>POST /jobs/versioning/backfill</c> tags every version the history declares at its earliest occurrence,
/// on the line of its major; a plan writes nothing, and a second run finds nothing to do.
/// </summary>
public class VersioningBackfillTests : WriterTestBase
{
    private static string Csproj(string version)
        => $"<Project Sdk=\"Microsoft.NET.Sdk\">\n<PropertyGroup><TargetFramework>net10.0</TargetFramework><Version>{version}</Version></PropertyGroup>\n</Project>\n";

    private static Dictionary<string, object?> Request(Origin origin, bool planOnly, string[]? move = null) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["defaultBranch"] = "main",
        ["tagger"] = new { name = "CodeCoverage versioning", email = "versioning@example.invalid" },
        ["settings"] = new VersioningSettingsWire { Lines = new ReleaseLinesWire { Text = ReleaseLinePattern.DefaultText } },
        ["planOnly"] = planOnly,
        ["move"] = move,
    };

    private async Task<VersioningBackfillResponse> BackfillAsync(Origin origin, bool planOnly, string[]? move = null)
    {
        var response = await Writer.Client.PostAsJsonAsync("/jobs/versioning/backfill", Request(origin, planOnly, move), GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VersioningBackfillResponse>(GitWriterHost.Json))!;
    }

    [Fact]
    public async Task The_history_is_tagged_at_each_versions_earliest_occurrence_and_a_plan_writes_nothing()
    {
        using var origin = new Origin(Server, "backfill");
        var first = origin.Write("src/Core/Core.csproj", Csproj("1.0.0")).Commit("core 1.0.0");
        var second = origin.Write("src/Core/Core.csproj", Csproj("1.1.0")).Commit("core 1.1.0");
        origin.Push("main");
        origin.Checkout("release/dotnet-1.x", "main").Push("release/dotnet-1.x");
        origin.Checkout("main").Write("src/Core/Core.csproj", Csproj("0.0.0-PLACEHOLDER")).Commit("onboarding");
        origin.Push("main");

        var planned = await BackfillAsync(origin, planOnly: true);
        planned.Outcome.Should().Be(JobOutcomes.Planned, planned.Detail);
        planned.Tags.Select(t => $"{t.Name}@{t.Commit}@{t.Branch}@{t.Outcome}").Should().Equal(
            $"Core/v1.0.0@{first}@release/dotnet-1.x@planned",
            $"Core/v1.1.0@{second}@release/dotnet-1.x@planned");
        origin.InBare("tag", "--list").Should().BeEmpty("a plan pushes nothing");

        var written = await BackfillAsync(origin, planOnly: false);
        written.Outcome.Should().Be(JobOutcomes.Tagged, written.Detail);
        written.Tags.Should().OnlyContain(t => t.Outcome == "created");
        origin.InBare("rev-parse", "Core/v1.0.0^{commit}").Trim().Should().Be(first);
        origin.InBare("rev-parse", "Core/v1.1.0^{commit}").Trim().Should().Be(second);

        (await BackfillAsync(origin, planOnly: false)).Tags.Should().BeEmpty("re-running Set up features backfills only what is missing (D8)");
    }

    [Fact]
    public async Task An_app_made_tag_away_from_its_earliest_occurrence_moves_only_when_the_owner_asks()
    {
        using var origin = new Origin(Server, "backfill-move");
        var declared = origin.Write("src/Core/Core.csproj", Csproj("1.0.0")).Commit("core 1.0.0");
        var later = origin.Write("src/Core/Core.csproj", Csproj("0.0.0-PLACEHOLDER")).Commit("onboarding");
        origin.Push("main");
        // The Tools shape (#638): the App released 1.0.0 again at a later commit.
        origin.InBare("-c", "user.name=CodeCoverage versioning", "-c", "user.email=versioning@example.invalid", "tag", "-a", "-m", "released", "Core/v1.0.0", later);

        var planned = await BackfillAsync(origin, planOnly: true);
        planned.Tags.Should().BeEmpty();
        planned.Collisions.Select(c => $"{c.Name}@{c.Current}->{c.Commit}@{c.Branch}@{c.Outcome}").Should().Equal($"Core/v1.0.0@{later}->{declared}@main@planned");

        (await BackfillAsync(origin, planOnly: false)).Collisions.Single().Outcome.Should().Be("left", "D9: a move only when the owner chose it");
        origin.InBare("rev-parse", "Core/v1.0.0^{commit}").Trim().Should().Be(later);

        var moved = await BackfillAsync(origin, planOnly: false, move: ["Core/v1.0.0"]);
        moved.Collisions.Single().Outcome.Should().Be("moved", moved.Collisions.Single().Detail);
        origin.InBare("rev-parse", "Core/v1.0.0^{commit}").Trim().Should().Be(declared);
        origin.InBare("for-each-ref", "--format=%(taggername)", "refs/tags/Core/v1.0.0").Trim().Should().Be("CodeCoverage versioning");

        (await BackfillAsync(origin, planOnly: true)).Collisions.Should().BeEmpty("the tag is at its earliest occurrence now");
    }

    /// <summary>
    /// docs/onboarding_backfill_partial_push_PRD.md D1-D3 (Tools #56, 2026-11-12): GitHub refused part of one tag push. The
    /// writer threw "did not report", so the other tags' outcomes, the moves and the remote's reason were all lost.
    /// </summary>
    [Fact]
    public async Task A_tag_the_remote_refuses_is_reported_with_its_reason_and_the_other_tags_and_the_moves_still_land()
    {
        using var origin = new Origin(Server, "backfill-refused");
        var core = origin.Write("src/Core/Core.csproj", Csproj("1.0.0")).Commit("core 1.0.0");
        var util = origin.Write("src/Util/Util.csproj", Csproj("1.0.0")).Commit("util 1.0.0");
        var later = origin.Write("src/Core/Core.csproj", Csproj("0.0.0-PLACEHOLDER")).Write("src/Util/Util.csproj", Csproj("0.0.0-PLACEHOLDER"))
            .Commit("onboarding");
        origin.Push("main");
        origin.InBare("-c", "user.name=CodeCoverage versioning", "-c", "user.email=versioning@example.invalid", "tag", "-a", "-m", "released", "Core/v1.0.0", later);
        // A third package whose tag the remote refuses below.
        var lib = origin.Write("src/Lib/Lib.csproj", Csproj("1.0.0")).Commit("lib 1.0.0");
        origin.Write("src/Lib/Lib.csproj", Csproj("0.0.0-PLACEHOLDER")).Commit("lib onboarding");
        origin.Push("main");
        origin.Refuse("refs/tags/Lib/v1.0.0");

        var result = await BackfillAsync(origin, planOnly: false, move: ["Core/v1.0.0"]);

        result.Outcome.Should().Be(JobOutcomes.Tagged, result.Detail);
        result.Tags.Select(t => $"{t.Name}@{t.Commit}@{t.Outcome}").Should().Equal($"Lib/v1.0.0@{lib}@rejected", $"Util/v1.0.0@{util}@created");
        result.Tags[0].Detail.Should().Contain("Lib/v1.0.0").And.Contain("remote rejected", "D1: the remote's own summary, not a guess");
        origin.Ref("refs/tags/Lib/v1.0.0").Should().BeNull();
        origin.InBare("rev-parse", "Util/v1.0.0^{commit}").Trim().Should().Be(util);

        result.Collisions.Single().Outcome.Should().Be("moved", "D2: the moves run whatever the writes did; " + result.Collisions.Single().Detail);
        origin.InBare("rev-parse", "Core/v1.0.0^{commit}").Trim().Should().Be(core);
    }

    /// <summary>D3: a move the remote refuses carries the remote's summary, not "changed since it was read".</summary>
    [Fact]
    public async Task A_move_the_remote_refuses_says_the_remote_refused_it()
    {
        using var origin = new Origin(Server, "backfill-move-refused");
        origin.Write("src/Core/Core.csproj", Csproj("1.0.0")).Commit("core 1.0.0");
        var later = origin.Write("src/Core/Core.csproj", Csproj("0.0.0-PLACEHOLDER")).Commit("onboarding");
        origin.Push("main");
        origin.InBare("-c", "user.name=CodeCoverage versioning", "-c", "user.email=versioning@example.invalid", "tag", "-a", "-m", "released", "Core/v1.0.0", later);
        origin.Refuse("refs/tags/Core/v1.0.0");

        var result = await BackfillAsync(origin, planOnly: false, move: ["Core/v1.0.0"]);

        result.Outcome.Should().Be(JobOutcomes.Tagged, result.Detail);
        var collision = result.Collisions.Single();
        collision.Outcome.Should().Be("conflict");
        collision.Detail.Should().Contain("remote rejected").And.NotContain("changed since it was read");
        origin.InBare("rev-parse", "Core/v1.0.0^{commit}").Trim().Should().Be(later);
    }
}
