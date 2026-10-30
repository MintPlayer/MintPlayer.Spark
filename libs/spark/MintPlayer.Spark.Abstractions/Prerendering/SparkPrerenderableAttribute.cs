namespace MintPlayer.Spark.Abstractions.Prerendering;

/// <summary>
/// Endpoint metadata: this endpoint is a read that server-side rendering may run in-process to supply
/// the response a page will request (docs/spark_ssr_PRD.md, M3).
/// <para>
/// The allow-list is explicit on purpose. The prerenderer dispatches a request through the endpoint's
/// own pipeline (routing, authorization, filters) under the visitor's identity; an endpoint without
/// this marker is refused, so a write can never run because a page happened to request it.
/// </para>
/// <para>⚠️ Only for endpoints without side effects. A read that gains one loses the marker in the same commit.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class SparkPrerenderableAttribute : Attribute
{
    /// <summary>The one instance endpoints add as metadata.</summary>
    public static SparkPrerenderableAttribute Instance { get; } = new();
}
