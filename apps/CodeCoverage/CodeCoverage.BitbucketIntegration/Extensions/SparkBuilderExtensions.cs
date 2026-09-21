using MintPlayer.Spark.Abstractions.Builder;

namespace CodeCoverage.BitbucketIntegration.Extensions;

/// <summary>
/// Bitbucket's entry point. Empty in stage 1 — the project exists so the shape is proven by more
/// than one case, and so the thing a port has to fill in is a file rather than a design decision.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Deliberately not called from <c>Program.cs</c> yet</b>, for the same reason as GitLab: a
/// registered forge nobody can sign in to advertises itself through
/// <c>IForgeIntegrationResolver</c> and leads to empty pages.
/// </para>
/// <para>
/// ⚠️ <b>The trap specific to Bitbucket is identity.</b> A workspace or repository <em>slug</em> is
/// renameable and is what appears in URLs, so it is the wrong thing to key documents by — the UUID
/// is the stable identifier. This matters more here than on the other two forges, because the
/// document-id scheme (D5) puts a numeric repository id in the key and Bitbucket does not have
/// one: a port has to decide what occupies that segment before it writes its first document, and
/// changing that decision afterwards is another re-key of the kind M6 exists to avoid repeating.
/// </para>
/// <para>
/// Bitbucket also has no equivalent of GitHub's check runs in the same shape, so
/// <c>Capabilities</c> is where that is declared honestly rather than worked around — the
/// conformance test fails a forge that claims what it cannot do.
/// </para>
/// </remarks>
public static class SparkBuilderExtensions
{
    /// <summary>
    /// Registers Bitbucket's implementations of the forge seams. A no-op until they exist.
    /// </summary>
    public static ISparkBuilder AddBitbucketIntegration(this ISparkBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder;
    }
}
