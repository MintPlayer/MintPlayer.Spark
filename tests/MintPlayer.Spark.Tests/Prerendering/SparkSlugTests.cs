using MintPlayer.Spark.Prerendering;

namespace MintPlayer.Spark.Tests.Prerendering;

/// <summary>
/// The server's slug (the 301 to the canonical URL, docs/spark_ssr_PRD.md D4) must equal ng-spark's <c>sparkSlug</c>
/// (po-detail/src/spark-slug.ts) character for character, or every page would redirect to a URL the page then rewrites.
/// </summary>
public class SparkSlugTests
{
    [Theory]
    [InlineData("Beyoncé - Halo!", "beyonce-halo")]
    [InlineData("A Sky Full of Stars", "a-sky-full-of-stars")]
    [InlineData("  --Mötley Crüe--  ", "motley-crue")]
    [InlineData("AC/DC", "ac-dc")]
    [InlineData("99 Luftballons", "99-luftballons")]
    [InlineData("!!!", "")]
    [InlineData(null, "")]
    public void The_slug_matches_the_client(string? breadcrumb, string expected)
        => SparkRoutesPrerenderContributor.SparkSlug(breadcrumb).Should().Be(expected);
}
