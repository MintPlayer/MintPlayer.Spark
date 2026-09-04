using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging.Services;

internal sealed class SparkMessagingBuilder(IServiceCollection services) : ISparkMessagingBuilder
{
    public IServiceCollection Services { get; } = services;

    public ISparkMessagingBuilder AddLane(Action<ILaneBuilder> configure)
    {
        Services.AddSparkLane(configure);
        return this;
    }

    public ISparkMessagingBuilder AddLane(Action<ILaneBuilder, IServiceProvider> configure)
    {
        Services.AddSparkLane(configure);
        return this;
    }

    public ISparkMessagingBuilder AddLane<TConfigurator>() where TConfigurator : class, ILaneConfigurator
    {
        Services.AddSparkLane<TConfigurator>();
        return this;
    }

    public ISparkMessagingBuilder MaxPartitionBlock(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));

        // Stored on options rather than on this builder, so it is also reachable from configuration
        // and so the registry has one place to read it from.
        Services.Configure<SparkMessagingOptions>(options => options.MaxPartitionBlock = budget);
        return this;
    }
}
