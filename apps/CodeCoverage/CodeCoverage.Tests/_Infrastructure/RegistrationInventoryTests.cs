using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.Infrastructure;

/// <summary>
/// Every service, recipient and cron job the running app depends on is actually registered.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This exists because the registrations are source-generated PER COMPILATION, and the
/// generated entry points are <c>internal</c>.</b> <c>AddCodeCoverage()</c>,
/// <c>spark.AddRecipients()</c>, <c>AddCronJobs()</c>, <c>AddCustomActions()</c> and
/// <c>AddActions()</c> each cover the app assembly and nothing else. So the moment a type moves to
/// another project — which is exactly what M15 does — it drops off its registration <b>silently</b>:
/// the app still compiles, still starts, and still serves pages. It just stops publishing pull
/// request comments, or stops reconciling state, or stops running a cron job, with no error
/// anywhere.
/// </para>
/// <para>
/// A compiler cannot catch it, a controller test cannot catch it, and a browser cannot catch it
/// either, because the symptom is the absence of a background effect. The only thing that can is an
/// inventory asserted against a real boot — which is what this is.
/// </para>
/// <para>
/// <b>Maintenance rule:</b> when a service is deliberately removed, delete its line here in the same
/// commit. Do not delete a line to make this pass.
/// </para>
/// </remarks>
public class RegistrationInventoryTests : CoverageRavenTest
{
    /// <summary>
    /// Asserts on the service DESCRIPTORS the real composition root produced, not on instances
    /// built from them.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Deliberately not <c>GetService</c>.</b> Resolving instead of inspecting answers a
    /// different and larger question — "can this be constructed, and does everything it transitively
    /// needs work?" — which drags the messaging subscription machinery into a test about
    /// registration. That machinery then NREs on host shutdown when it was never fully started
    /// (<c>MessageSubscriptionManager.StopMessagingAsync</c>), turning a passing assertion into a
    /// failing test for a reason that has nothing to do with what is being asserted.
    /// <para>
    /// The descriptor is also the more precise instrument: the failure this guards against is a
    /// type moving to another assembly and dropping off a per-compilation generated registration.
    /// That shows up as a MISSING DESCRIPTOR, exactly, with no construction required.
    /// </para>
    /// </remarks>
    private void AssertRegistered(params Type[] serviceTypes)
    {
        using var store = GetDocumentStore();

        // ⚠ The outer factory is NOT disposed here, and that is not an oversight.
        // WithWebHostBuilder returns a second factory that shares the parent's logger provider, so
        // disposing both tears the same EventLog down twice and the second attempt throws
        // ObjectDisposedException from inside the logging pipeline - a failure with no relation to
        // what is being asserted. The outer factory is never built (nothing touches its Services),
        // so it owns nothing that needs releasing.
        var factory = new CoverageWebAppFactory(store);

        IServiceCollection? captured = null;
        using var probe = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => captured = services));

        // WithWebHostBuilder is lazy; touching Services runs the composition root, which is what
        // populates `captured`. The host is built but never started, so nothing begins polling.
        _ = probe.Services;

        captured.Should().NotBeNull("the composition root should have run");

        var registered = captured!.Select(descriptor => descriptor.ServiceType).ToHashSet();
        var missing = serviceTypes.Where(t => !registered.Contains(t)).Select(t => t.Name).ToList();

        missing.Should().BeEmpty(
            "live code resolves every one of these at runtime, and an unregistered one fails at the "
            + "point of use rather than at startup — silently, in a background worker nobody is watching");
    }

    /// <summary>
    /// The forge seam. If M15 moves an implementation to another assembly without re-registering
    /// it, this is what says so.
    /// </summary>
    [Fact]
    public void The_forge_services_are_registered() => AssertRegistered(
        typeof(IForgeIntegrationResolver),
        typeof(IForgeClient),
        typeof(IForgeAccessService),
        typeof(IForgeFeedbackPublisher),
        typeof(IRepositoryResolver),
        typeof(IPullRequestCommentPublisher),
        typeof(IPullRequestCommentGateway));

    /// <summary>
    /// The GitHub-specific services. Named separately from the seam above because these are the
    /// ones M15 physically relocates.
    /// </summary>
    [Fact]
    public void The_github_services_are_registered() => AssertRegistered(
        typeof(IGitHubAccessService),
        typeof(IGitHubAppReadinessService),
        typeof(IGitHubStateReconciler),
        typeof(IGitHubProjectCards),
        typeof(IInstallationProjects),
        typeof(IInstallationRepositories),
        typeof(IGitHubContentService),
        typeof(IGitHubDiffService),
        typeof(IGitHubUserTokenService));

    /// <summary>
    /// The app's own services, which have no forge in them but share the one generated
    /// registration method — so they fail the same way for the same reason.
    /// </summary>
    [Fact]
    public void The_application_services_are_registered() => AssertRegistered(
        typeof(IMyAccountsService));

    /// <summary>
    /// Message handlers. A wire type whose handler is missing is <b>dropped silently</b> — the
    /// message is consumed and nothing happens, which is indistinguishable from a quiet queue.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>GitHubEventsRecipient</c> is the one M15 must move, and it is registered through the
    /// generated <c>internal AddRecipients()</c> over the APP assembly. Moving it without giving
    /// the new project its own builder extension stops every GitHub webhook being handled, while
    /// the endpoint keeps answering 200.
    /// </remarks>
    [Fact]
    public void The_webhook_and_ingestion_recipients_are_registered() => AssertRegistered(
        typeof(IRecipient<global::MintPlayer.Spark.Webhooks.GitHub.Messages.GitHubWebhookMessage>),
        typeof(IRecipient<global::CodeCoverage.Ingestion.ReconcileAccountMessage>),
        typeof(IRecipient<global::CodeCoverage.Ingestion.ParseSessionMessage>),
        typeof(IRecipient<global::CodeCoverage.Ingestion.FinalizeBuildMessage>),
        typeof(IRecipient<global::CodeCoverage.Ingestion.AssembleCommitMessage>),
        typeof(IRecipient<global::CodeCoverage.Feedback.PublishFeedbackMessage>),
        typeof(IRecipient<global::CodeCoverage.Feedback.PublishPullRequestCommentMessage>),
        typeof(IRecipient<global::CodeCoverage.Feedback.OpenPullRequestCommentMessage>));
}
