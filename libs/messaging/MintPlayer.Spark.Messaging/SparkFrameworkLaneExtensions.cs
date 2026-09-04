using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Services;

namespace MintPlayer.Spark.Messaging;

/// <summary>
/// Lets a framework package declare a lane of its own.
/// </summary>
/// <remarks>
/// Applications declare lanes through <c>AddMessaging(..., lanes: …)</c>. A framework package such as
/// replication cannot: it runs in its own <c>Add…</c> call and must not require the application to
/// declare a lane it does not own. This reaches the registry that <c>AddSparkMessaging</c> registered
/// and declares into it directly.
/// </remarks>
public static class SparkFrameworkLaneExtensions
{
    /// <summary>
    /// Declares a lane owned by the framework, if messaging is present.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when messaging has not been added — the caller decides whether that is
    /// a problem. Nothing is thrown, because registration order between two <c>Add…</c> calls is the
    /// application's business.
    /// </returns>
    public static bool TryDeclareFrameworkLane(
        this IServiceCollection services,
        string laneName,
        Action<IQueueBuilder> configure)
    {
        var registry = services
            .FirstOrDefault(d => d.ServiceType == typeof(LaneRegistry))?
            .ImplementationInstance as LaneRegistry;

        if (registry is null)
            return false;

        configure(registry.Declare(laneName));
        return true;
    }
}
