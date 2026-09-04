using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Services;

namespace MintPlayer.Spark.Tests.Messaging;

/// <summary>
/// SPIKE for docs/lane_attributes_PRD.md. The test strategy for the lane source generator rests on
/// comparing a GENERATED configurator against a hand-written one <i>behaviourally</i> — same lanes,
/// same plans, same partition keys — rather than comparing emitted text. This proves the comparison
/// is possible and, more importantly, that it can fail.
/// </summary>
/// <remarks>
/// <para>
/// The constraint that forces this shape: <c>PartitionBy</c> takes an
/// <c>Expression&lt;Func&lt;T,string&gt;&gt;</c> but <c>LaneDeclaration.PartitionBy</c> calls
/// <c>Compile()</c> immediately and keeps only a <c>Func&lt;object,string&gt;</c>. There is no
/// expression tree left to compare, so two selectors can only be compared by <b>running them</b> on
/// sample messages. That is the better assertion anyway: it compares the key each would produce,
/// not the syntax that produced it.
/// </para>
/// <para>
/// Both sides here are hand-written, because the generator does not exist yet. The point is the
/// harness, not the result — when the generator lands, one side is replaced by its output and every
/// assertion below keeps its meaning.
/// </para>
/// </remarks>
public class LaneEquivalenceSpike
{
    private record ParseSession(string BuildId);
    private record FinalizeBuild(string BuildId);
    private record DeleteBuilds(long RepositoryGitHubId, int PullRequestNumber);

    private const string Lane = "coverage-parse-session";
    private const string DeleteLane = "coverage-delete-pr-builds";

    /// <summary>Stands in for what the generator will emit from attributes.</summary>
    private static void Generated(ILaneBuilder lanes)
    {
        lanes.Queue(Lane)
            .Ordered()
            .PartitionBy<ParseSession>(m => m.BuildId)
            .PartitionBy<FinalizeBuild>(m => m.BuildId)
            .MaxPartitionsInFlight(2);

        lanes.Queue(DeleteLane)
            .Ordered()
            // A composite key, which the generator must compose from two [PartitionedBy] properties.
            .PartitionBy<DeleteBuilds>(m => $"{m.RepositoryGitHubId}/{m.PullRequestNumber}")
            .MaxPartitionsInFlight(4);
    }

    /// <summary>The declaration as it is written by hand today.</summary>
    private static void HandWritten(ILaneBuilder lanes)
    {
        lanes.Queue(Lane)
            .Ordered()
            .PartitionBy<ParseSession>(m => m.BuildId)
            .PartitionBy<FinalizeBuild>(m => m.BuildId)
            .MaxPartitionsInFlight(2);

        lanes.Queue(DeleteLane)
            .Ordered()
            .PartitionBy<DeleteBuilds>(m => $"{m.RepositoryGitHubId}/{m.PullRequestNumber}")
            .MaxPartitionsInFlight(4);
    }

    [Fact]
    public void A_generated_declaration_is_comparable_to_a_hand_written_one()
    {
        Snapshot(Registry(Generated)).Should().BeEquivalentTo(Snapshot(Registry(HandWritten)));
    }

    [Fact]
    public void The_comparison_detects_a_changed_partition_key()
    {
        // Guards the assertion above from being vacuous. A snapshot that cannot tell these apart
        // would pass for any generator output at all, which is worse than having no test: it would
        // read as a green regression fence over an unchecked code path.
        var divergent = Snapshot(Registry(lanes =>
        {
            lanes.Queue(Lane)
                .Ordered()
                .PartitionBy<ParseSession>(m => m.BuildId)
                // The bug this whole PR was about: finalize keyed apart from the parses of its build.
                .PartitionBy<FinalizeBuild>(m => $"finalize-{m.BuildId}")
                .MaxPartitionsInFlight(2);

            lanes.Queue(DeleteLane)
                .Ordered()
                .PartitionBy<DeleteBuilds>(m => $"{m.RepositoryGitHubId}/{m.PullRequestNumber}")
                .MaxPartitionsInFlight(4);
        }));

        divergent.Should().NotBeEquivalentTo(Snapshot(Registry(HandWritten)));
    }

    [Fact]
    public void The_comparison_detects_a_changed_lane_policy()
    {
        var divergent = Snapshot(Registry(lanes =>
        {
            lanes.Queue(Lane)
                .Ordered()
                .PartitionBy<ParseSession>(m => m.BuildId)
                .PartitionBy<FinalizeBuild>(m => m.BuildId)
                .MaxPartitionsInFlight(8);   // 2 in the baseline

            lanes.Queue(DeleteLane)
                .Ordered()
                .PartitionBy<DeleteBuilds>(m => $"{m.RepositoryGitHubId}/{m.PullRequestNumber}")
                .MaxPartitionsInFlight(4);
        }));

        divergent.Should().NotBeEquivalentTo(Snapshot(Registry(HandWritten)));
    }

    [Fact]
    public void The_comparison_detects_a_changed_retry_ladder()
    {
        // Retry schedules are reference-unequal, so the snapshot has to PROBE the ladder rather than
        // compare the objects. Without probing this case passes silently.
        var divergent = Snapshot(Registry(lanes =>
        {
            lanes.Queue(Lane)
                .Ordered()
                .PartitionBy<ParseSession>(m => m.BuildId)
                .PartitionBy<FinalizeBuild>(m => m.BuildId)
                .MaxPartitionsInFlight(2)
                .Retry(RetrySchedule.Ladder("1s 2s"));

            lanes.Queue(DeleteLane)
                .Ordered()
                .PartitionBy<DeleteBuilds>(m => $"{m.RepositoryGitHubId}/{m.PullRequestNumber}")
                .MaxPartitionsInFlight(4);
        }));

        divergent.Should().NotBeEquivalentTo(Snapshot(Registry(HandWritten)));
    }

    /// <summary>One lane, flattened to values that two registries can be compared on.</summary>
    /// <remarks>
    /// A named type rather than an anonymous one, so the failure message names the member that
    /// differs. The negative cases above are what prove the comparison can fail at all.
    /// </remarks>
    private sealed record LaneSnapshot(
        string Lane,
        bool Ordered,
        int MaxInFlight,
        int MaxParkedPartitions,
        string[] Retry,
        string[] Keys);

    /// <summary>
    /// Everything about a registry that a consumer can observe, flattened so two are comparable.
    /// </summary>
    private static LaneSnapshot[] Snapshot(LaneRegistry registry)
    {
        object[] samples =
        [
            new ParseSession("builds/1"),
            new FinalizeBuild("builds/1"),
            new DeleteBuilds(42, 7),
        ];

        return registry.DeclaredLanes
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(lane =>
            {
                var plan = registry.PlanFor(lane);
                return new LaneSnapshot(
                    lane,
                    plan.Ordered,
                    plan.MaxInFlight,
                    plan.MaxParkedPartitions,
                    // Probed, not compared by reference.
                    Enumerable.Range(1, 8).Select(a => plan.Retry.Next(a).ToString()!).ToArray(),
                    samples.Select(s => $"{s.GetType().Name}={KeyOrNull(registry, lane, s)}").ToArray());
            })
            .ToArray();
    }

    /// <summary>
    /// A selector is declared per (lane, message type), so asking a lane for a type it does not carry
    /// is expected and must not fail the snapshot.
    /// </summary>
    private static string KeyOrNull(LaneRegistry registry, string lane, object sample)
    {
        try
        {
            return registry.PartitionKeyFor(lane, sample.GetType(), sample) ?? "<null>";
        }
        catch (InvalidOperationException)
        {
            return "<unbound>";
        }
    }

    private static LaneRegistry Registry(Action<ILaneBuilder> declare)
    {
        var services = new ServiceCollection();
        services.AddSparkLane(declare);

        var options = Options.Create(new SparkMessagingOptions());
        services.AddSingleton(options);

        return new LaneRegistry(services.BuildServiceProvider(), options);
    }
}
