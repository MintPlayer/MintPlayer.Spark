using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Moderation.Endpoints;
using MintPlayer.Spark.Moderation.Jobs;
using MintPlayer.Spark.Moderation.Services;

namespace MintPlayer.Spark.Moderation;

/// <summary>Registration for moderation.</summary>
public static class SparkModerationExtensions
{
    /// <summary>
    /// Adds moderation for every <see cref="IModeratable"/> entity: votes and the reputation ledger,
    /// privileges as <c>security.json</c> groups, the ten fraud defences, flags and the review queue,
    /// locks, suspensions, the audit log, the account-deletion handler and <c>/spark/moderation/*</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Configuration is <c>Spark:Moderation</c>. When the builder's configuration is a
    /// <see cref="IConfigurationBuilder"/> (a <c>WebApplicationBuilder</c>'s is), <c>App_Data/moderation.json</c>
    /// is added as its lowest-precedence source; otherwise call
    /// <see cref="SparkModerationConfigurationExtensions.AddSparkModerationFile"/> yourself.
    /// <paramref name="configure"/> sets code defaults, which configuration then overrides (D14).
    /// </para>
    /// <para>
    /// Grant the rights in <c>security.json</c> (<see cref="ModerationRights"/>); run the app with
    /// <c>--spark-init-moderation</c> for the list. Startup is refused when the layered configuration
    /// does not validate against <c>security.json</c>.
    /// </para>
    /// </remarks>
    public static ISparkBuilder AddModeration<TUser>(this ISparkBuilder builder, Action<SparkModerationOptions>? configure = null)
        where TUser : SparkUser
    {
        if (builder.Configuration is IConfigurationBuilder configurationBuilder)
            configurationBuilder.AddSparkModerationFile();

        var services = builder.Services;
        var optionsBuilder = services.AddOptions<SparkModerationOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);
        // After the code defaults: configuration wins (D14).
        optionsBuilder.BindConfiguration(SparkModerationConfigurationExtensions.SectionName);

        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.TryAddScoped<ModerationTargets>();
        services.TryAddScoped<ModerationUserState>();
        services.TryAddScoped<ReputationLedger>();
        services.TryAddScoped<ModerationAudit>();
        services.TryAddScoped<ModerationIpHasher>();
        services.TryAddScoped<FraudDetector>();
        services.TryAddScoped<ModerationReview>();
        services.TryAddScoped<ISparkModeration, SparkModeration>();
        services.TryAddScoped<ISparkModerationJobs, SparkModerationJobs>();
        services.TryAddScoped<IModerationAccounts, IdentityModerationAccounts<TUser>>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkAccountDeletionHandler<TUser>, ModerationAccountDeletionHandler<TUser>>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkPersonalDataContributor<TUser>, ModerationPersonalDataContributor<TUser>>());

        builder.AddGroupMembershipProvider<ModerationPrivilegeProvider>();
        builder.AddPersistentObjectInterceptor<ModerationInterceptor>();
        // The same suspension and lock checks for documents written on the caller's behalf that are not
        // PO saves (a contribution written while the caller saves its target, contributions M5).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISatelliteWriteGuard, ModerationSatelliteWriteGuard>());
        // ...and the audit of moderator actions on them (a removed version, a revert; contributions M5b).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISatelliteAuditSink, ModerationSatelliteAuditSink>());
        builder.AddIndexesFrom(typeof(SparkModerationExtensions).Assembly);

        // Schedules are read at registration (the Cron registry needs them now): code, then configuration.
        var registrationTime = new SparkModerationOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkModerationConfigurationExtensions.SectionName + ":Jobs").Bind(registrationTime.Jobs);
        var jobs = registrationTime.Jobs;
        builder.AddCron(cron => cron
            .AddJob<ModerationCreditingJob>(jobs.CreditingSchedule, "Moderation.Crediting")
            .AddJob<ModerationFraudDetectorJob>(jobs.DetectorSchedule, "Moderation.FraudDetector"));

        builder.Registry.AddMiddleware(app => ModerationStartupCheck.Run(app.ApplicationServices));
        // The per-library method the Endpoints generator emits for this assembly (AssemblyInfo.cs).
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkModerationEndpoints());
        return builder;
    }
}
