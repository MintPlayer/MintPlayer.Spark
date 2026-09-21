using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;

namespace CodeCoverage.GithubIntegration.Extensions;

/// <summary>
/// The one entry point this assembly exposes. Modelled on
/// <c>MintPlayer.Spark.Webhooks.GitHub</c>'s <c>AddGithubWebhooks</c>, which is the in-repo
/// precedent for a module that contributes services and recipients from its own assembly.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This exists because the generated registrations are per-compilation and
/// <c>internal</c>.</b> <c>AddCodeCoverageGithubIntegration()</c> and <c>AddRecipients()</c> are
/// emitted into <em>this</em> assembly and cover only the <c>[Register]</c> and
/// <c>IRecipient&lt;T&gt;</c> types declared here. The application's own
/// <c>AddCodeCoverage()</c> / <c>spark.AddRecipients()</c> cannot see them, so without this method
/// every service in this project would simply not be registered — and nothing would say so. The
/// app would start, serve pages, and quietly stop publishing pull-request comments and handling
/// GitHub webhooks.
/// </para>
/// <para>
/// A method rather than an assembly scan, deliberately: a scan would make "is this registered?"
/// depend on whether the assembly happened to be loaded, which is exactly the kind of question
/// that has no answer at 3am. <c>RegistrationInventoryTests</c> asserts the result either way.
/// </para>
/// <para>
/// <b>Not registered here:</b> the GitHub webhook endpoint itself (that is
/// <c>AddGithubWebhooks</c>, a Spark library, and stays in the app's composition root where its
/// secret and paths are configured), and anything requiring <c>security.json</c> or the model —
/// both are application-only by design (PRD §6.9).
/// </para>
/// </remarks>
public static class SparkBuilderExtensions
{
    /// <summary>
    /// Registers GitHub's implementations of the forge seams, plus this assembly's recipients.
    /// </summary>
    public static ISparkBuilder AddGithubIntegration(this ISparkBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The [Register] attributes in this assembly.
        builder.Services.AddCodeCoverageGithubIntegration();

        // The IRecipient<T> implementations in this assembly. Generated internal, which is why the
        // call has to happen from inside it.
        builder.AddRecipients();

        return builder;
    }
}
