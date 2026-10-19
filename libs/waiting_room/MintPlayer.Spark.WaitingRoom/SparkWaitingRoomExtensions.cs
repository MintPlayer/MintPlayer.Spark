using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Challenges;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.WaitingRoom.Admissions;
using MintPlayer.Spark.WaitingRoom.Identity;
using MintPlayer.Spark.WaitingRoom.Live;
using MintPlayer.Spark.WaitingRoom.Services;
using MintPlayer.Spark.WaitingRoom.Worker;

namespace MintPlayer.Spark.WaitingRoom;

/// <summary>
/// The waiting room (<c>docs/commerce_PRD.md</c> §4.12, <c>docs/guide-waiting-room.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();
///     spark.AddWaitingRoom();                              // Spark:WaitingRoom (+ the risk pipeline of Challenges)
///     spark.AddChallenges(c => c.UseAltcha());             // optional: a challenge at join, the risk signals
/// });
/// await rooms.SaveRoomAsync("summer-fest", new WaitingRoomDefinition
/// {
///     Gate = "SalePhases/1", Mode = WaitingRoomMode.RandomizedPreQueue, RequireSignIn = true,
///     OpensAt = start.AddHours(-1), StartsAt = start, CapacityKey = "quotas/summer-fest",
/// });
/// // Checkout (or a host route) refuses holds for the gate without a token: IWaitingRoomAdmissions.
/// </code>
/// </summary>
public static class SparkWaitingRoomExtensions
{
    /// <summary>
    /// Registers <see cref="ISparkWaitingRooms"/>, <see cref="IWaitingRoomAdmissions"/>, the admission worker (cron), the live
    /// topics <c>waiting-room:{key}</c>, the visitor merge, the GDPR account hooks (export and erasure of the account's
    /// entries), <c>/spark/waiting-room/*</c>, and the risk pipeline
    /// (<c>AddChallenges()</c> without providers: no challenge is asked until the host picks one). Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddWaitingRoom(this ISparkBuilder builder, Action<SparkWaitingRoomOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<WaitingRoomMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        services.AddSparkConfigurationSection<SparkWaitingRoomOptions>();
        if (configure is not null)
            services.Configure(configure);

        builder.AddChallenges();
        services.AddHttpContextAccessor();
        services.AddDataProtection();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<WaitingRoomTokens>();
        services.TryAddSingleton<WaitingRoomPublisher>();
        services.TryAddSingleton<WaitingRoomGateCache>();
        services.TryAddScoped<ISparkWaitingRooms, SparkWaitingRooms>();
        services.TryAddScoped<IWaitingRoomAdmissions, WaitingRoomAdmissions>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISparkLiveTopicPolicy, WaitingRoomTopicPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkVisitorMergeHandler, WaitingRoomVisitorMerge>());
        // GDPR Art. 15/17/20 through the account hooks: the export section "waiting-room" and the erasure.
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkPersonalDataContributor<>), typeof(WaitingRoomPersonalData<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkAccountDeletionHandler<>), typeof(WaitingRoomAccountDeletion<>)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WaitingRoomSeeder>());

        // The schedule is read at registration (the Cron registry needs it now): code, then configuration.
        var registrationTime = new SparkWaitingRoomOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkWaitingRoomOptions.SectionName).Bind(registrationTime);
        builder.AddCron(cron => cron.AddJob<WaitingRoomWorker>(registrationTime.WorkerSchedule, "WaitingRoom.Worker"));

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices.GetRequiredService<IOptions<SparkWaitingRoomOptions>>().Value));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkWaitingRoomEndpoints());
        return builder;
    }

    /// <summary>
    /// Refuses a hidden-tab presence window under 20 s (WCAG 2.2.1: at least 20 seconds for a simple action) and any
    /// configured room whose definition is invalid (including a fair mode without sign-in, D10).
    /// </summary>
    internal static void ValidateStartup(SparkWaitingRoomOptions options)
    {
        var problems = new List<string>();
        if (options.HiddenPresenceWindow < TimeSpan.FromSeconds(20))
            problems.Add($"Spark:WaitingRoom:HiddenPresenceWindow is {options.HiddenPresenceWindow.TotalSeconds:0} s; WCAG 2.2.1 gives the user at least 20 seconds for the single action that keeps their turn.");
        if (options.PresenceTimeout <= TimeSpan.Zero)
            problems.Add("Spark:WaitingRoom:PresenceTimeout must be positive.");
        if (options.BatchSize is < 1 or > 1024)
            problems.Add("Spark:WaitingRoom:BatchSize must be between 1 and 1024.");
        foreach (var (key, room) in options.Rooms)
        {
            if (!WaitingRoomStore.IsValidKey(key))
                problems.Add($"Spark:WaitingRoom:Rooms:{key}: a room key is lower-case letters, digits, '.', '_' and '-'.");
            else if (SparkWaitingRooms.Validate(room) is { } problem)
                problems.Add($"Spark:WaitingRoom:Rooms:{key}: {problem}");
        }
        if (problems.Count > 0)
            throw new InvalidOperationException("The waiting room cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    private sealed class WaitingRoomMarker;
}

/// <summary>Upserts the rooms declared under <c>Spark:WaitingRoom:Rooms</c> at startup (configuration beats code, §4.2).</summary>
internal sealed class WaitingRoomSeeder(IServiceProvider services, IOptions<SparkWaitingRoomOptions> options, ILogger<WaitingRoomSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Rooms.Count == 0)
            return;
        await using var scope = services.CreateAsyncScope();
        var rooms = scope.ServiceProvider.GetRequiredService<ISparkWaitingRooms>();
        foreach (var (key, room) in options.Value.Rooms)
        {
            try
            {
                await rooms.SaveRoomAsync(key.ToLowerInvariant(), room, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Configured waiting room {Room} could not be saved.", key);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
