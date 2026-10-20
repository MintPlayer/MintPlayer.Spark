using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Approvals.Identity;
using MintPlayer.Spark.Approvals.Jobs;
using MintPlayer.Spark.Approvals.Proposals;
using MintPlayer.Spark.Approvals.Services;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Approvals;

/// <summary>
/// Approvals (<c>docs/commerce_PRD.md</c> §4.13, <c>docs/guide-approvals.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();
///     spark.AddApprovals();          // Spark:Approvals; libraries that raise requests call it themselves
/// });
/// // A kind: deciders + handler, discovered in the app (or registered by its library).
/// public sealed class RefundApproval : IApprovalKind { public static string Name => "shop.refund"; }
/// await approvals.SubmitAsync&lt;RefundApproval&gt;(new ApprovalSubmission { Subject = new("Order", order.Id!), Title = "Refund €40" });
/// </code>
/// </summary>
public static class SparkApprovalsExtensions
{
    /// <summary>
    /// Registers <see cref="ISparkApprovals"/>, the <c>ApprovalRequest</c> type and its actions, document proposals
    /// for the types under <c>Spark:Approvals:Types</c>, the notices, the sweep and <c>/spark/approvals/*</c>. Safe to
    /// call more than once (each library that raises requests calls it). Needs <c>AddAuthentication&lt;TUser&gt;()</c>.
    /// </summary>
    public static ISparkBuilder AddApprovals(this ISparkBuilder builder, Action<SparkApprovalsOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<ApprovalsMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        services.AddSparkConfigurationSection<SparkApprovalsOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        // Kinds are read from the registrations themselves, once the container is complete.
        services.TryAddSingleton(new ApprovalKindCatalog(services));
        services.TryAddScoped<ApprovalVisibility>();
        services.TryAddScoped<ApprovalNotices>();
        services.TryAddScoped<SparkApprovals>();
        services.TryAddScoped<ISparkApprovals>(sp => sp.GetRequiredService<SparkApprovals>());
        services.TryAddScoped<IApprovalDeciders<DocumentProposalApproval>, DocumentProposalDeciders>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISparkLiveTopicPolicy, ApprovalLiveTopicPolicy>());

        builder.AddInterceptor<DocumentProposals>();

        // Open generics: they close over whichever user type AddAuthentication<TUser>() registered.
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkAccountDeletionHandler<>), typeof(ApprovalAccountDeletion<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkPersonalDataContributor<>), typeof(ApprovalPersonalData<>)));

        MintPlayer.Spark.MailManager.SparkMailTemplateServiceCollectionExtensions.AddSparkMailTemplates(
            services, typeof(SparkApprovalsExtensions).Assembly, "SparkMail/");

        // The schedule is read at registration (the Cron registry needs it now): code, then configuration.
        var registrationTime = new SparkApprovalsOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkApprovalsOptions.SectionName).Bind(registrationTime);
        builder.AddCron(cron => cron.AddJob<ApprovalSweepJob>(registrationTime.SweepSchedule, "Approvals.Sweep"));

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkApprovalsEndpoints());
        // ApprovalRequest is IExpiring (the retention): UseSpark() enables expiration for any IExpiring model type.
        return builder;
    }

    /// <summary>A kind without deciders or handler, two kinds of one name, or a proposal type the model lacks refuse startup.</summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        var problems = services.GetRequiredService<ApprovalKindCatalog>().Problems().ToList();
        var modelLoader = services.GetRequiredService<IModelLoader>();
        foreach (var (type, governed) in services.GetRequiredService<IOptions<SparkApprovalsOptions>>().Value.Types)
        {
            if (!governed.Create && !governed.Modify)
                continue;
            if (modelLoader.GetEntityTypeByName(type) is not { } definition)
                problems.Add($"Spark:Approvals:Types names '{type}', which is not a persistent-object type of this application.");
            else if (string.IsNullOrEmpty(definition.ClrType))
                problems.Add($"Spark:Approvals:Types names '{type}', a virtual type: it is never saved, so nothing can be proposed.");
        }
        if (problems.Count > 0)
            throw new InvalidOperationException("Approvals cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    private sealed class ApprovalsMarker;
}
