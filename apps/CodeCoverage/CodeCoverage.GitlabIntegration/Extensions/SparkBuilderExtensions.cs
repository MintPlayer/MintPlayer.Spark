using MintPlayer.Spark.Abstractions.Builder;

namespace CodeCoverage.GitlabIntegration.Extensions;

/// <summary>
/// GitLab's entry point. Empty in stage 1 — the project exists so the shape is proven by more than
/// one case, and so the thing a port has to fill in is a file rather than a design decision.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Deliberately not called from <c>Program.cs</c> yet.</b> Registering a forge that cannot
/// authenticate anyone would put a GitLab unit in the sidebar leading to an empty page, and would
/// make <c>IForgeIntegrationResolver</c> answer "yes, I have GitLab" to code that then finds
/// nothing behind it. The composition root gains one line here when there is something to
/// register — see <c>CodeCoverage.GithubIntegration</c> for what that looks like.
/// </para>
/// <para>
/// <b>What a port fills in</b>, per the forge conformance contract:
/// </para>
/// <list type="bullet">
/// <item><description>an <c>IForgeIntegration</c> whose <c>Provider</c> is
/// <c>EForgeProvider.GitLab</c> and whose <c>Capabilities</c> are honest — the conformance test
/// fails a forge that claims a capability it does not implement;</description></item>
/// <item><description>an <c>IForgeAccessService</c> answering which namespaces the viewer may
/// manage, and it must never consult another forge's state (D4);</description></item>
/// <item><description>the OIDC scheme, contributed as startup configuration from this method
/// rather than as an interface member (PRD §6.10).</description></item>
/// </list>
/// <para>
/// ⚠️ Two GitLab-specific traps already recorded: a merge request's <c>iid</c> is per-project and
/// is the number a human sees, while <c>id</c> is global — using the wrong one addresses somebody
/// else's merge request; and namespaces nest (documented to 20 levels) and are slash-delimited,
/// which is why <see cref="CodeCoverage.Forge.ForgeOwner"/> joins with a colon and the two-slot
/// <c>{owner}/{name}</c> route shape does not survive here unchanged.
/// </para>
/// </remarks>
public static class SparkBuilderExtensions
{
    /// <summary>
    /// Registers GitLab's implementations of the forge seams. A no-op until they exist.
    /// </summary>
    public static ISparkBuilder AddGitlabIntegration(this ISparkBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Nothing to register yet. When there is, it is the generated
        // builder.Services.AddCodeCoverageGitlabIntegration() plus builder.AddRecipients(),
        // exactly as the GitHub project does — both are internal to their own assembly, which is
        // the whole reason this method has to exist here rather than in the app.
        return builder;
    }
}
