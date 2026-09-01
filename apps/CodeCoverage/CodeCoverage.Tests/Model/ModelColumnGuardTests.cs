using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// #13 U5: the generic Spark grids show exactly the attributes whose
/// <c>showedOn</c> contains <c>Query</c>, ordered by <c>order</c>.
/// <para>
/// Regression pin for MintPlayer.Spark#274, fixed upstream in
/// <c>10.0.0-preview.54</c>: synchronize used to re-derive <c>showedOn</c> from
/// index projection membership on every run for <c>[FromIndex]</c> entities,
/// silently wiping the curated per-surface trims (that is exactly how
/// <c>45354c0</c> regressed the account and commit pages). It now narrows but
/// never widens a hand-trimmed value. CI's <c>--spark-verify-model</c> still
/// does not hash <c>showedOn</c>, so this test remains the only loud failure if
/// the upstream guarantee ever regresses; if it trips after a synchronize,
/// re-apply the curated values below (see docs/adoption-findings.md M6) and
/// report it upstream rather than editing this test.
/// </para>
/// </summary>
public class ModelColumnGuardTests
{
    private static JsonElement LoadPersistentObject(string entity)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MintPlayer.Spark.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        dir.Should().NotBeNull("the test must run somewhere under the repository root");
        var path = Path.Combine(dir!, "apps", "CodeCoverage", "CodeCoverage", "App_Data", "Model", $"{entity}.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("persistentObject").Clone();
    }

    private static string[] VisibleQueryColumns(JsonElement persistentObject)
        => [.. persistentObject.GetProperty("attributes").EnumerateArray()
            .Where(a => a.GetProperty("isVisible").GetBoolean()
                && a.GetProperty("showedOn").GetString()!.Contains("Query"))
            .OrderBy(a => a.GetProperty("order").GetInt32())
            .Select(a => a.GetProperty("name").GetString()!)];

    [Fact]
    public void Repository_grid_shows_the_curated_columns_with_the_repository_name_first()
    {
        // The row link lives on the first visible column; Account/OwnerLogin are
        // constant on the account page and belong on the detail view only.
        VisibleQueryColumns(LoadPersistentObject("Repository")).Should().Equal(
            "Name", "LatestCoverage", "FullName", "LatestCoverageSha");
    }

    [Fact]
    public void Build_grid_shows_the_curated_columns()
    {
        VisibleQueryColumns(LoadPersistentObject("Build")).Should().Equal(
            "Run", "Status", "Sessions", "Coverage", "CreatedAtUtc");
    }
}
