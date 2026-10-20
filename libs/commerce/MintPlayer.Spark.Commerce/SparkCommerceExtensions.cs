using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Audit;
using MintPlayer.Spark.Commerce.Identity;
using MintPlayer.Spark.Commerce.Jobs;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Commerce.Retention;
using MintPlayer.Spark.Commerce.Services;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Organizations;

namespace MintPlayer.Spark.Commerce;

/// <summary>
/// The commerce core (<c>docs/commerce_PRD.md</c> §4.1, M4):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();
///     spark.AddCommerce();           // Spark:Commerce — channels, fees, sellers, legal documents
/// });
/// // App_Data/security.json: "bindings": { "commerce:platform-admins": ["Administrators"], "commerce:notice-reviewers": ["Moderators"] }
/// </code>
/// </summary>
public static class SparkCommerceExtensions
{
    /// <summary>
    /// Registers the commerce core. Also adds Organizations (sellers are organizations, with the custom roles
    /// <c>finance</c> and the export role), Approvals (registration, trader checks, complaints) and Cron (DAC7
    /// reminders, retention). Callable more than once (packages call it): <paramref name="configure"/> calls are code
    /// defaults, applied in order, and the <c>Spark:Commerce</c> section is bound after them, so <b>configuration
    /// wins</b> (§4.2, Moderation's order).
    /// </summary>
    public static ISparkBuilder AddCommerce(this ISparkBuilder builder, Action<SparkCommerceOptions>? configure = null)
    {
        var services = builder.Services;
        var defaults = services.GetOrAddSparkSingleton(() => new CommerceCodeDefaults(), out var first);
        if (configure is not null)
            defaults.Actions.Add(configure);
        if (!first)
            return builder;

        services.AddOptions<SparkCommerceOptions>();
        services.AddSingleton<IConfigureOptions<SparkCommerceOptions>>(sp => new ConfigureNamedOptions<SparkCommerceOptions>(Options.DefaultName, options =>
        {
            foreach (var action in defaults.Actions)
                action(options);
            sp.GetService<IConfiguration>()?.GetSection(SparkCommerceOptions.SectionName).Bind(options);
        }));

        builder.AddOrganizations();
        builder.AddOrganizationRole(CommerceRights.FinanceRole, "Sees the organization's sales, payouts and fees, and exports its buyers.");
        builder.AddOrganizationRole("seller-admin", "Manages the organization's seller profile and exports its buyers and attendees.");
        builder.AddApprovals();

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);

        // Pricing (default 4, GA3).
        services.TryAddSingleton<IFinalPriceCalculator, FinalPriceCalculator>();
        services.TryAddScoped<ISparkPriceHistory, SparkPriceHistory>();
        services.TryAddScoped<ICommercePriceDisplay, CommercePriceDisplayService>();
        builder.AddInterceptor<PriceHistoryInterceptors>();

        // Audit and retention (default 8, §4.15).
        services.TryAddScoped<ICommerceAudit, CommerceAudit>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRetentionFloorSource, CommerceRetentionFloors>());
        services.TryAddSingleton<ICommerceRetention, CommerceRetention>();

        // Sellers (D21, GA5, GA6, GB14) and legal documents (GA2).
        services.TryAddScoped<CommerceAccess>();
        services.TryAddScoped<SparkLegalDocuments>();
        services.TryAddScoped<ISparkLegalDocuments>(sp => sp.GetRequiredService<SparkLegalDocuments>());
        services.TryAddScoped<LegalDocumentEditor>();
        services.TryAddScoped<ISparkSellers, SparkSellers>();
        services.TryAddScoped<SellerRegistration>();
        services.TryAddScoped<SellerExports>();
        services.TryAddScoped<DsaNotices>();
        services.TryAddScoped<ICommerceSellerNotifier, LoggingSellerNotifier>();
        services.TryAddScoped<IApprovalDeciders<SellerRegistrationApproval>, PlatformAdminDeciders<SellerRegistrationApproval>>();
        services.TryAddScoped<IApprovalHandler<SellerRegistrationApproval>, SellerRegistrationHandler>();
        services.TryAddScoped<IApprovalDeciders<TraderVerificationApproval>, PlatformAdminDeciders<TraderVerificationApproval>>();
        services.TryAddScoped<IApprovalHandler<TraderVerificationApproval>, TraderVerificationHandler>();
        services.TryAddScoped<IApprovalDeciders<SellerComplaintApproval>, PlatformAdminDeciders<SellerComplaintApproval>>();
        services.TryAddScoped<IApprovalHandler<SellerComplaintApproval>, SellerComplaintHandler>();

        // GDPR hooks (default 7) and the organization's end of relationship.
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkAccountDeletionHandler<>), typeof(CommerceAccountDeletion<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkPersonalDataContributor<>), typeof(CommercePersonalData<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOrganizationDataHandler, CommerceOrganizationData>());

        builder.AddCron(cron =>
        {
            cron.AddJob<Dac7ReminderJob>(Dac7ReminderJob.CronSchedule, "Commerce.Dac7Reminders");
            cron.AddJob<CommerceRetentionJob>(CommerceRetentionJob.CronSchedule, "Commerce.Retention");
        });

        services.AddHostedService<CommerceStartupWarnings>();
        builder.Registry.AddMiddleware(app => CommerceStartupValidation.Validate(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkCommerceEndpoints());
        return builder;
    }

    /// <summary>The <c>AddCommerce(o => …)</c> callbacks, in call order.</summary>
    private sealed class CommerceCodeDefaults
    {
        public List<Action<SparkCommerceOptions>> Actions { get; } = [];
    }
}
