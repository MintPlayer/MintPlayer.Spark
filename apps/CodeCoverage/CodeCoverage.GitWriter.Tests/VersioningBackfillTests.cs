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

    private static Dictionary<string, object?> Request(Origin origin, bool planOnly) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["defaultBranch"] = "main",
        ["tagger"] = new { name = "CodeCoverage versioning", email = "versioning@example.invalid" },
        ["settings"] = new VersioningSettingsWire { Lines = new ReleaseLinesWire { Text = ReleaseLinePattern.DefaultText } },
        ["planOnly"] = planOnly,
    };

    private async Task<VersioningBackfillResponse> BackfillAsync(Origin origin, bool planOnly)
    {
        var response = await Writer.Client.PostAsJsonAsync("/jobs/versioning/backfill", Request(origin, planOnly), GitWriterHost.Json);
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
}
