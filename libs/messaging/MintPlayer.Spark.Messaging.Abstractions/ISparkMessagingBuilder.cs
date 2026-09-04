using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Configures messaging: its lanes, and the settings that apply across them.
/// </summary>
public interface ISparkMessagingBuilder
{
    /// <summary>The service collection, so a lane configurator can register its own dependencies.</summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Declares one or more lanes with a delegate.
    /// </summary>
    /// <remarks>
    /// The delegate runs <b>lazily</b>, once, after the container is built — not during
    /// <c>AddMessaging</c> — so the <see cref="IServiceProvider"/> handed to the overload below is a
    /// real one. Use <see cref="AddLane{TConfigurator}"/> when the configuration needs constructor
    /// injection rather than a service-locator call.
    /// </remarks>
    ISparkMessagingBuilder AddLane(Action<ILaneBuilder> configure);

    /// <summary>Declares lanes with access to the resolved container.</summary>
    /// <example>
    /// <code>
    /// messaging.AddLane((lanes, services) =>
    /// {
    ///     var options = services.GetRequiredService&lt;IOptions&lt;MailOptions&gt;&gt;().Value;
    ///     lanes.Queue("spark-email")
    ///          .Concurrent(options.Workers)
    ///          .Retry(RetrySchedule.Ladder(options.RetryLadder));
    /// });
    /// </code>
    /// </example>
    ISparkMessagingBuilder AddLane(Action<ILaneBuilder, IServiceProvider> configure);

    /// <summary>
    /// Declares lanes with a class, which is constructed by the container and may inject anything.
    /// </summary>
    ISparkMessagingBuilder AddLane<TConfigurator>() where TConfigurator : class, ILaneConfigurator;

    /// <summary>
    /// The longest any one partition may be blocked by its own retry schedule before startup refuses
    /// the configuration. Defaults to fifteen minutes.
    /// </summary>
    /// <remarks>
    /// Under an ordered lane a failing head blocks its partition until it succeeds or dead-letters,
    /// so a schedule's total <i>is</i> that partition's worst-case downtime. A lane that genuinely
    /// wants a longer one says so with <c>AcceptPartitionBlock</c>.
    /// </remarks>
    ISparkMessagingBuilder MaxPartitionBlock(TimeSpan budget);
}
