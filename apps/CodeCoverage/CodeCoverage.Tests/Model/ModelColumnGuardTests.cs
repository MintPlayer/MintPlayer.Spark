using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Services;
using NSubstitute;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// #13 U5: the generic Spark grids show the curated columns, in the curated order.
/// <para>
/// Regression pin for MintPlayer.Spark#274, fixed upstream in
/// <c>10.0.0-preview.54</c>: synchronize used to re-derive <c>showedOn</c> from
/// index projection membership on every run for <c>[FromIndex]</c> entities,
/// silently wiping the curated per-surface trims (that is exactly how
/// <c>45354c0</c> regressed the account and commit pages). It now narrows but
/// never widens a hand-trimmed value. CI's <c>--spark-verify-model</c> still
/// does not hash <c>showedOn</c> or <c>columnOrder</c>, so this test remains the only loud
/// failure if the upstream guarantee ever regresses; if it trips after a synchronize,
/// re-apply the curated values below (see docs/adoption-findings.md M6) and
/// report it upstream rather than editing this test.
/// </para>
/// <para>
/// The columns come from the framework's own loader and <c>QueryResultProjector.BuildColumns</c>, the
/// single place every grid's columns are computed (#284 R3), so <c>showedOn</c>, <c>order</c> and a
/// query's <c>columnOrder</c> are applied exactly as at runtime.
/// </para>
/// </summary>
public class ModelColumnGuardTests
{
    private static readonly Lazy<IModelLoader> Loader = new(CreateLoader);

    private static IModelLoader CreateLoader()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MintPlayer.Spark.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        dir.Should().NotBeNull("the test must run somewhere under the repository root");

        var hostEnvironment = Substitute.For<IHostEnvironment>();
        hostEnvironment.ContentRootPath.Returns(Path.Combine(dir!, "apps", "CodeCoverage", "CodeCoverage"));
        return new ModelLoader(ModelSource.For(hostEnvironment, []), TranslationsLoader.For(hostEnvironment, []),
            NullLogger<ModelLoader>.Instance);
    }

    private static string[] GridColumns(string entity, string? queryName = null)
    {
        var definition = Loader.Value.GetEntityTypeByName(entity);
        definition.Should().NotBeNull($"{entity}.json must load");

        var query = queryName is null ? null : Loader.Value.GetQueries().SingleOrDefault(q => q.Name == queryName);
        if (queryName is not null) query.Should().NotBeNull($"query {queryName} must exist");

        return [.. QueryResultProjector.BuildColumns(definition!, query).Select(c => c.Name)];
    }

    [Fact]
    public void Repositories_page_shows_the_repository_name_first_then_its_account()
    {
        // The row link lives on the first visible column. IsPrivate is the narrow lock column right after
        // the name and account (#264, G-Q4): it used to be shipped in the row without a column so
        // repo-name could draw a badge, which showedOn cannot say. Account is on the query surface since
        // #284 R8, so the all-repositories grid says whose repository each row is.
        GridColumns("Repository", "GetRepositories").Should().Equal(
            "Name", "Account", "IsPrivate", "LatestCoverage", "FullName", "LatestCoverageSha");
    }

    [Theory]
    [InlineData("Account_Repositories")]
    [InlineData("ApiToken_SelectableRepositories")]
    public void Account_scoped_repository_grids_hide_the_constant_account_column(string queryName)
    {
        // Both are scoped to one account (the account page; the token's own account in the picker), so
        // an Account column would repeat the same value on every row. columnOrder lists it as !Account,
        // and the linked first column is the repository name again (#284 R8).
        GridColumns("Repository", queryName).Should().Equal(
            "Name", "IsPrivate", "LatestCoverage", "FullName", "LatestCoverageSha");
    }

    [Fact]
    public void Build_grid_shows_the_curated_columns()
    {
        GridColumns("Build").Should().Equal(
            "Run", "Status", "Sessions", "Coverage", "CreatedAtUtc");
    }
}
