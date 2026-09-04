using MintPlayer.SourceGenerators.Attributes;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging.Services;

internal interface ILaneRegistry
{
    /// <summary>The resolved plan for a lane, including defaults for lanes nobody declared.</summary>
    LanePlan PlanFor(string laneName);

    /// <summary>The ordering domain of one message, or <see langword="null"/> on an unordered lane.</summary>
    string? PartitionKeyFor(string laneName, Type messageType, object message);

    IReadOnlyCollection<string> DeclaredLanes { get; }

    /// <summary>Throws on a configuration that cannot work. Called once at startup.</summary>
    void Validate(IEnumerable<Type> knownMessageTypes);
}

/// <summary>
/// Resolves lane declarations from the container, once, on first use.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why lazily, rather than during <c>AddMessaging</c>.</b> Declarations used to be built while the
/// service collection was still being assembled, which meant a lane could not be configured from
/// anything the application had registered, and a framework package had to reach into
/// <see cref="IServiceCollection"/> to find an already-constructed registry — making the result
/// depend on the order the <c>Add…</c> calls ran in, silently. Now every declaration arrives as an
/// <see cref="ILaneConfigurator"/> registration, so order does not matter and a configurator may
/// inject whatever it needs.
/// </para>
/// <para>
/// <b>Why singleton, when the configurators are resolved from a provider.</b> What a lane declares is
/// process-wide: a name, a mode, a concurrency limit, a schedule. Building it late is useful;
/// keeping it short-lived is not, and caching per-scope objects beyond their scope would be a
/// captive-dependency bug. Per-request state belongs in <i>handlers</i>, which are scoped and
/// resolved per message.
/// </para>
/// </remarks>
internal sealed partial class LaneRegistry : ILaneRegistry
{
    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly IOptions<SparkMessagingOptions> options;

    private Lazy<Declarations> declarations = null!;

    /// <summary>Plans are pure functions of the declarations, so they are worth caching.</summary>
    private readonly ConcurrentDictionary<string, LanePlan> planCache = new(StringComparer.OrdinalIgnoreCase);

    /// <remarks>
    /// The <see cref="Lazy{T}"/> cannot be a field initialiser because it closes over an instance
    /// method, so it is built here — after injection completes — rather than in a hand-written
    /// constructor.
    /// <para>
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>: configurators run exactly once
    /// however many threads arrive together, and they will — the feeder starts lanes while the first
    /// broadcast is already resolving a partition key.
    /// </para>
    /// </remarks>
    [PostConstruct]
    private void InitializeDeclarations()
        => declarations = new Lazy<Declarations>(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    private Declarations Build()
    {
        var builder = new LaneBuilder();

        // Resolved from the root provider: a configurator is a singleton, so it may inject singletons
        // and options but not scoped services — which is correct, because a lane's declaration is not
        // request-shaped.
        foreach (var configurator in services.GetServices<ILaneConfigurator>())
            configurator.Configure(builder);

        return builder.Build();
    }

    public IReadOnlyCollection<string> DeclaredLanes => [.. declarations.Value.Lanes.Keys];

    public LanePlan PlanFor(string laneName) => planCache.GetOrAdd(laneName, name =>
    {
        var messagingOptions = options.Value;

        var overrideSchedule = string.IsNullOrWhiteSpace(messagingOptions.RetryOverride)
            ? null
            : RetrySchedule.Ladder(messagingOptions.RetryOverride);

        var plan = declarations.Value.Lanes.TryGetValue(name, out var declaration)
            ? declaration.ToPlan(messagingOptions.ResolvedDefaultRetry)
            // An undeclared lane is CONCURRENT, never ordered. A silently-ordered lane with no
            // partition key would put every message into one partition and serialize the whole lane —
            // the exact failure partitioning exists to prevent — so the default has to be the mode
            // that cannot be silently wrong.
            : new LanePlan
            {
                LaneName = name,
                Ordered = false,
                MaxInFlight = 1,
                Retry = messagingOptions.ResolvedDefaultRetry,
            };

        // Applied last, because the override's whole purpose is to be one switch that reaches every
        // lane: a test environment cannot be asked to restate each lane's schedule.
        return overrideSchedule is null ? plan : plan with { Retry = overrideSchedule };
    });

    public string? PartitionKeyFor(string laneName, Type messageType, object message)
    {
        if (!declarations.Value.Lanes.TryGetValue(laneName, out var declaration) || !declaration.IsOrdered)
            return null;

        var key = declaration.PartitionKeyFor(messageType, message);

        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException(
                $"The partition selector for '{messageType.FullName}' on ordered lane '{laneName}' returned an "
                + "empty key. An empty key would put every message of this lane into one partition and serialize "
                + "the whole lane, which is exactly what partitioning exists to avoid.");

        return key;
    }

    public void Validate(IEnumerable<Type> knownMessageTypes)
    {
        var resolved = declarations.Value;

        foreach (var messageType in knownMessageTypes)
        {
            var laneName = QueueNames.ForMessageType(messageType);
            if (!resolved.Lanes.TryGetValue(laneName, out var declaration) || !declaration.IsOrdered)
                continue;

            if (!declaration.HasSelectorFor(messageType))
                throw new InvalidOperationException(
                    $"Message type '{messageType.FullName}' is on ordered lane '{laneName}' but declares no "
                    + $"partition key. Add .PartitionBy<{messageType.Name}>(m => …) to the lane, or make the "
                    + "lane Concurrent. Without a key the lane cannot know which messages depend on each other.");
        }

        foreach (var declaration in resolved.Lanes.Values.Where(d => d.IsOrdered))
        {
            var worstCase = declaration.WorstCaseBlock(options.Value.ResolvedDefaultRetry);
            var budget = declaration.AcceptedBlock ?? options.Value.MaxPartitionBlock;

            if (worstCase > budget)
                throw new InvalidOperationException(
                    $"Lane '{declaration.LaneName}' is ordered and its retry schedule can block one partition for "
                    + $"{RetrySchedule.Describe(worstCase)}, beyond the {RetrySchedule.Describe(budget)} ceiling. "
                    + "Under Ordered, a failing head blocks its partition until it succeeds or dead-letters, so the "
                    + "schedule's total IS the partition's downtime. Either shorten the schedule, make the lane "
                    + "Concurrent (nothing waits behind a parked message), or call .AcceptPartitionBlock("
                    + $"TimeSpan.FromMinutes({Math.Ceiling(worstCase.TotalMinutes)})) to say the wait is intended.");
        }
    }

    private sealed record Declarations(IReadOnlyDictionary<string, LaneDeclaration> Lanes);

    /// <summary>Collects declarations while the configurators run.</summary>
    private sealed class LaneBuilder : ILaneBuilder
    {
        private readonly Dictionary<string, LaneDeclaration> lanes = new(StringComparer.OrdinalIgnoreCase);

        public IQueueBuilder Queue<TMessage>() => Declare(QueueNames.ForMessageType(typeof(TMessage)));

        public IQueueBuilder Queue(string laneName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(laneName);

            // The name is no longer interpolated into a subscription query, so RQL injection is not
            // the risk it was — but a name that reaches configuration keys and metric labels still
            // has to be a name.
            if (!QueueNames.IsValid(laneName))
                throw new ArgumentException(
                    $"'{laneName}' is not a valid lane name. Lane names must match [A-Za-z0-9._+`-]+.", nameof(laneName));

            return Declare(laneName);
        }

        private IQueueBuilder Declare(string laneName)
        {
            if (lanes.ContainsKey(laneName))
                throw new InvalidOperationException(
                    $"Lane '{laneName}' is declared twice. Each lane must be declared exactly once, so that "
                    + "its delivery mode and retry policy have a single owner.");

            var declaration = new LaneDeclaration(laneName);
            lanes[laneName] = declaration;
            return declaration;
        }

        public Declarations Build() => new(lanes);
    }

    private sealed class LaneDeclaration(string laneName) : IQueueBuilder, IOrderedQueueBuilder, IConcurrentQueueBuilder
    {
        private readonly Dictionary<Type, Func<object, string>> selectors = [];

        public string LaneName { get; } = laneName;
        public bool IsOrdered { get; private set; }
        public TimeSpan? AcceptedBlock { get; private set; }

        private int maxInFlight = 1;
        private IRetrySchedule? retry;

        public IOrderedQueueBuilder Ordered()
        {
            IsOrdered = true;
            // A sensible default for an unknown machine: a fixed literal would give a 1-vCPU host the
            // same concurrency as a 32-core one.
            maxInFlight = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            return this;
        }

        public IConcurrentQueueBuilder Concurrent(int maxConcurrency)
        {
            if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
            IsOrdered = false;
            maxInFlight = maxConcurrency;
            return this;
        }

        public IConcurrentQueueBuilder Unbounded()
        {
            IsOrdered = false;
            maxInFlight = 1024;
            return this;
        }

        public IOrderedQueueBuilder PartitionBy<TMessage>(Expression<Func<TMessage, string>> key)
        {
            ArgumentNullException.ThrowIfNull(key);
            var compiled = key.Compile();
            selectors[typeof(TMessage)] = message => compiled((TMessage)message);
            return this;
        }

        public IOrderedQueueBuilder MaxPartitionsInFlight(int partitions)
        {
            if (partitions < 1) throw new ArgumentOutOfRangeException(nameof(partitions));
            maxInFlight = partitions;
            return this;
        }

        public IOrderedQueueBuilder AcceptPartitionBlock(TimeSpan budget)
        {
            AcceptedBlock = budget;
            return this;
        }

        IOrderedQueueBuilder IOrderedQueueBuilder.Retry(IRetrySchedule schedule)
        {
            retry = schedule ?? throw new ArgumentNullException(nameof(schedule));
            return this;
        }

        IConcurrentQueueBuilder IConcurrentQueueBuilder.Retry(IRetrySchedule schedule)
        {
            retry = schedule ?? throw new ArgumentNullException(nameof(schedule));
            return this;
        }

        public bool HasSelectorFor(Type messageType) => selectors.ContainsKey(messageType);

        public string? PartitionKeyFor(Type messageType, object message)
            => selectors.TryGetValue(messageType, out var selector) ? selector(message) : null;

        /// <summary>The sum of every delay this lane's schedule can impose before it gives up.</summary>
        public TimeSpan WorstCaseBlock(IRetrySchedule defaultSchedule)
        {
            var schedule = retry ?? defaultSchedule;
            var total = TimeSpan.Zero;

            for (var attempt = 1; attempt <= 1000; attempt++)
            {
                if (schedule.Next(attempt) is not RetryDecision.RetryAfter retryAfter)
                    break;
                total += retryAfter.Delay;
            }

            return total;
        }

        public LanePlan ToPlan(IRetrySchedule defaultSchedule) => new()
        {
            LaneName = LaneName,
            Ordered = IsOrdered,
            MaxInFlight = maxInFlight,
            Retry = retry ?? defaultSchedule,
            PartitionSelectors = selectors,
        };
    }
}
