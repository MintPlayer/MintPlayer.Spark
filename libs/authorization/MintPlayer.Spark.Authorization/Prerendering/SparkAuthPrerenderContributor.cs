using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Authorization.Prerendering;

/// <summary>
/// Server-side rendering (docs/spark_ssr_PRD.md): every page's shell asks who is signed in and which
/// sign-in methods exist (spark-auth.service.ts), so the page renders with the visitor's own menu and
/// sign-in state.
/// </summary>
internal sealed class SparkAuthPrerenderContributor : ISparkPrerenderContributor
{
    public Task ContributeAsync(ISparkPrerenderContext context) => Task.WhenAll(
        context.GetAsync("/spark/auth/me"),
        context.GetAsync("/spark/auth/capabilities"));
}
