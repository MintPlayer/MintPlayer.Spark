using System.Linq.Expressions;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Collects lane declarations while the application is being configured, then resolves them into
/// <see cref="LanePlan"/>s and validates the result.
/// </summary>
internal sealed class LaneRegistry
{
    private readonly Dictionary<string, LaneDeclaration> declarations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which lane a message type belongs to, learned from the types named in <c>PartitionBy</c> and
    /// from recipient discovery. Used to validate that every type on an ordered lane has a selector.
    /// </summary>
    private readonly Dictionary<Type, string> laneByMessageType = [];

    public IQueueBuilder Declare(string laneName)
    {
        if (declarations.ContainsKey(laneName))
            throw new InvalidOperationException(
                $"Lane '{laneName}' is declared twice. Each lane must be declared exactly once, so that "
                + "its delivery mode and retry policy have a single owner.");

        var declaration = new LaneDeclaration(laneName);
        declarations[laneName] = declaration;
        return declaration;
    }

    public void BindMessageTypeToLane(Type messageType, string laneName) => laneByMessageType[messageType] = laneName;

    /// <summary>
    /// Produces the plan for a lane, applying defaults for lanes nobody declared.
    /// </summary>
    /// <remarks>
    /// An undeclared lane is <b>concurrent</b>, never ordered. A silently-ordered lane with no
    /// partition key would serialize everything on one key — the exact failure this design exists to
    /// prevent — so the default has to be the mode that cannot be silently wrong.
    /// </remarks>
    public LanePlan PlanFor(string laneName, IRetrySchedule? defaultSchedule = null, IRetrySchedule? overrideSchedule = null)
    {
        var plan = declarations.TryGetValue(laneName, out var declaration)
            ? declaration.ToPlan(defaultSchedule ?? RetrySchedule.Default)
            : new LanePlan
            {
                LaneName = laneName,
                Ordered = false,
                MaxInFlight = 1,
                Retry = defaultSchedule ?? RetrySchedule.Default,
            };

        // The override is applied last and wins over everything, because its whole purpose is to be
        // one switch that reaches every lane — a test environment cannot be asked to restate each
        // lane's schedule.
        return overrideSchedule is null ? plan : plan with { Retry = overrideSchedule };
    }

    public IReadOnlyCollection<string> DeclaredLanes => declarations.Keys;

    /// <summary>
    /// The ordering domain of one message, or <see langword="null"/> on an unordered lane.
    /// </summary>
    /// <remarks>
    /// Runs producer-side, exactly once, and the answer is persisted. A selector must therefore be
    /// pure over the payload: nothing re-runs it, so one that changed its mind would silently split a
    /// partition in half.
    /// </remarks>
    public string? PartitionKeyFor(string laneName, Type messageType, object message)
    {
        if (!declarations.TryGetValue(laneName, out var declaration) || !declaration.IsOrdered)
            return null;

        var key = declaration.PartitionKeyFor(messageType, message);

        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException(
                $"The partition selector for '{messageType.FullName}' on ordered lane '{laneName}' returned an "
                + "empty key. An empty key would put every message of this lane into one partition and serialize "
                + "the whole lane, which is exactly what partitioning exists to avoid.");

        return key;
    }

    /// <summary>
    /// Fails startup when an ordered lane carries a message type with no partition selector.
    /// </summary>
    /// <remarks>
    /// Loud, at startup, naming the type — because the alternative is silent misordering, which is
    /// only ever noticed as corrupted downstream data.
    /// </remarks>
    public void Validate(IEnumerable<Type> knownMessageTypes, TimeSpan maxPartitionBlock)
    {
        foreach (var messageType in knownMessageTypes)
        {
            var laneName = QueueNames.ForMessageType(messageType);
            if (!declarations.TryGetValue(laneName, out var declaration) || !declaration.IsOrdered)
                continue;

            if (!declaration.HasSelectorFor(messageType))
                throw new InvalidOperationException(
                    $"Message type '{messageType.FullName}' is on ordered lane '{laneName}' but declares no "
                    + $"partition key. Add .PartitionBy<{messageType.Name}>(m => …) to the lane, or make the "
                    + "lane Concurrent. Without a key the lane cannot know which messages depend on each other.");
        }

        foreach (var declaration in declarations.Values.Where(d => d.IsOrdered))
        {
            var worstCase = declaration.WorstCaseBlock();
            var budget = declaration.AcceptedBlock ?? maxPartitionBlock;

            if (worstCase > budget)
                throw new InvalidOperationException(
                    $"Lane '{declaration.LaneName}' is ordered and its retry schedule can block one partition for "
                    + $"{RetrySchedule.Describe(worstCase)}, beyond the {RetrySchedule.Describe(budget)} ceiling. "
                    + "Under Ordered, a failing head blocks its partition until it succeeds or dead-letters, so the "
                    + "schedule's total IS the partition's downtime. Either shorten the schedule, make the lane "
                    + $"Concurrent (nothing waits behind a parked message), or call .AcceptPartitionBlock("
                    + $"TimeSpan.FromMinutes({Math.Ceiling(worstCase.TotalMinutes)})) to say the wait is intended.");
        }
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

        /// <summary>The sum of every delay the schedule can impose before it gives up.</summary>
        public TimeSpan WorstCaseBlock()
        {
            var total = TimeSpan.Zero;
            for (var attempt = 1; attempt <= 1000; attempt++)
            {
                if ((retry ?? RetrySchedule.Default).Next(attempt) is not RetryDecision.RetryAfter retryAfter)
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
