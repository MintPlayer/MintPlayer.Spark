using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Moderation.Documents;
using MintPlayer.Spark.Moderation.Indexes;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.Moderation;

/// <summary>
/// #460 M12 spikes S-MOD-A (map-reduce indexes from an add-on assembly) and S-MOD-E (vote + ledger
/// entry atomic on the shared request session). Kept as tests so the answers stay pinned.
/// </summary>
public class ModerationSpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    private readonly List<IAsyncDisposable> factories = [];

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private SparkEndpointFactory<MsSpikeContext> Start()
    {
        var factory = new SparkEndpointFactory<MsSpikeContext>(
            Store,
            [],
            // Exactly what AddModeration does: declare the add-on assembly; core's UseSpark deploys it.
            configureSpark: spark => spark.AddIndexesFrom(typeof(Moderation_VotePairs).Assembly));
        factories.Add(factory);
        return factory;
    }

    [Fact]
    public async Task S_MOD_A_map_reduce_indexes_deploy_from_the_add_on_assembly_and_reduce_correctly()
    {
        Start();

        // 1. Deployed by core's startup, from the add-on assembly, with no error state.
        var names = await Store.Maintenance.SendAsync(new GetIndexNamesOperation(0, 256));
        foreach (var expected in ModerationIndexNames.All) names.Should().Contain(expected);
        foreach (var name in ModerationIndexNames.All)
        {
            var errors = await Store.Maintenance.SendAsync(new GetIndexErrorsOperation([name]));
            errors.Single().Errors.Should().BeEmpty($"{name} must deploy cleanly");
            var definition = await Store.Maintenance.SendAsync(new GetIndexOperation(name));
            output.WriteLine($"{name}: type={definition.Type}");
        }

        // 2. Catch-up time over a realistic volume: 40 voters × 50 authors, 5 votes each way-ish.
        const int voters = 40, authors = 50, votesPerPair = 2;
        var start = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var written = 0;
        var seed = Stopwatch.StartNew();
        using (var bulk = Store.BulkInsert())
        {
            for (var v = 0; v < voters; v++)
            for (var a = 0; a < authors; a++)
            for (var n = 0; n < votesPerPair; n++)
            {
                var castAt = start.AddHours(v + a + n);
                var voteId = $"ModerationVotes/u{v}/q{a}-{n}";
                await bulk.StoreAsync(new ModerationVote
                {
                    Id = voteId, VoterId = $"u{v}", AuthorId = $"a{a}", TargetId = $"q{a}-{n}", TargetType = "Q",
                    Direction = (v + n) % 5 == 0 ? -1 : 1, CastAtUtc = castAt, Day = ModerationIds.Day(castAt),
                });
                await bulk.StoreAsync(new ReputationEvent
                {
                    Id = voteId + "/e", UserId = $"a{a}", VoterId = $"u{v}", Kind = "UpvoteReceived", Points = 10, VoteWeight = 1,
                    Day = ModerationIds.Day(castAt), CreatedAtUtc = castAt, CreditableAfterUtc = castAt.AddHours(48),
                    Credited = n == 0,
                });
                written += 2;
            }
            // Reciprocal pair: a0 and u0 as the same people voting for each other.
            await bulk.StoreAsync(new ModerationVote
            {
                Id = "ModerationVotes/a0/q-u0", VoterId = "a0", AuthorId = "u0", TargetId = "q-u0", TargetType = "Q",
                Direction = 1, CastAtUtc = start, Day = ModerationIds.Day(start),
            });
            // Withdrawn vote: must not count.
            await bulk.StoreAsync(new ModerationVote
            {
                Id = "ModerationVotes/a0/q-u0-2", VoterId = "a0", AuthorId = "u0", TargetId = "q-u0-2", TargetType = "Q",
                Direction = 0, CastAtUtc = start, Day = ModerationIds.Day(start),
            });
            written += 2;
        }
        seed.Stop();

        var catchUp = Stopwatch.StartNew();
        await Store.WaitForIndexingAsync(timeout: TimeSpan.FromMinutes(2), expectedIndexes: ModerationIndexNames.All);
        catchUp.Stop();
        output.WriteLine($"S-MOD-A: {written} documents bulk-inserted in {seed.ElapsedMilliseconds} ms; all four indexes non-stale after {catchUp.ElapsedMilliseconds} ms.");

        using var session = Store.OpenAsyncSession();

        // voter→target counts: u1→a3 has 2 votes (n = 0, 1), spread over the days the hours fall on.
        var pair = await session.Query<Moderation_VotePairs.Result, Moderation_VotePairs>()
            .Where(r => r.VoterId == "u1" && r.AuthorId == "a3").ToListAsync();
        pair.Sum(r => r.Count).Should().Be(votesPerPair);

        // Reciprocal pair: u0→a0 exists (from the grid) and a0→u0 exists (seeded), the withdrawn one does not count.
        var reverse = await session.Query<Moderation_VotePairs.Result, Moderation_VotePairs>()
            .Where(r => r.VoterId == "a0" && r.AuthorId == "u0").ToListAsync();
        reverse.Sum(r => r.Count).Should().Be(1, "a withdrawn vote (Direction 0) is not mapped");

        // Rep sums: a7 received 40 voters × 2 entries, of which n == 0 is credited → 40 × 10.
        var cells = await session.Query<Moderation_ReputationCells.Result, Moderation_ReputationCells>()
            .Where(r => r.UserId == "a7").ToListAsync();
        cells.Sum(c => c.Points).Should().Be(voters * 10);
        cells.Select(c => c.VoterId).Distinct().Count().Should().Be(voters);

        var pending = await session.Query<Moderation_PendingReputation.Result, Moderation_PendingReputation>()
            .Where(r => r.UserId == "a7").SingleAsync();
        pending.Points.Should().Be(voters * 10, "the n == 1 entries are not credited yet");

        var due = await session.Query<Moderation_EntriesToCredit.Result, Moderation_EntriesToCredit>()
            .Where(r => r.CreditableAfterUtc <= start.AddHours(48 + 3))
            .OfType<ReputationEvent>()
            .CountAsync();
        due.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task S_MOD_E_a_vote_its_ledger_entry_and_the_tally_commit_together_on_the_request_session()
    {
        var factory = Start();

        // Happy path: one SaveChanges on the scoped (shared actions) session writes all three.
        using (var scope = factory.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IAsyncDocumentSession>();
            await StoreVoteAsync(session, "u1", "q1");
            var before = session.Advanced.NumberOfRequests;
            await session.SaveChangesAsync();
            (session.Advanced.NumberOfRequests - before).Should().Be(1, "one batch");
        }
        (await ExistsAsync(ModerationIds.Vote("u1", "q1"))).Should().BeTrue();
        (await ExistsAsync(ModerationIds.VoteEvent(ModerationIds.Vote("u1", "q1"), 1, "recipient"))).Should().BeTrue();
        (await ExistsAsync(ModerationIds.Tally("q1"))).Should().BeTrue();

        // Race: our request decided "no vote yet", then another request stored the same vote first.
        using (var scope = factory.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IAsyncDocumentSession>();
            (await session.LoadAsync<ModerationVote>(ModerationIds.Vote("u2", "q1"))).Should().BeNull();
            await StoreVoteAsync(session, "u2", "q1", tallyUp: 2);

            using (var other = Store.OpenAsyncSession())
            {
                await other.StoreAsync(new ModerationVote { VoterId = "u2", TargetId = "q1", Direction = 1 }, ModerationIds.Vote("u2", "q1"));
                await other.SaveChangesAsync();
            }

            var act = () => session.SaveChangesAsync();
            await act.Should().ThrowAsync<ConcurrencyException>();
        }

        (await ExistsAsync(ModerationIds.VoteEvent(ModerationIds.Vote("u2", "q1"), 1, "recipient")))
            .Should().BeFalse("the ledger entry is in the same transaction as the refused vote");
        using (var check = Store.OpenAsyncSession())
            (await check.LoadAsync<ModerationTally>(ModerationIds.Tally("q1")))!.Up.Should().Be(1, "the tally write was rolled back too");
    }

    private static async Task StoreVoteAsync(IAsyncDocumentSession session, string voter, string target, int tallyUp = 1)
    {
        var voteId = ModerationIds.Vote(voter, target);
        // "" = must not exist yet: the deterministic id plus this check is the double-vote guard.
        await session.StoreAsync(new ModerationVote { VoterId = voter, TargetId = target, Direction = 1, Revision = 1 }, string.Empty, voteId);
        await session.StoreAsync(new ReputationEvent { UserId = "a", VoterId = voter, Kind = "UpvoteReceived", Points = 10 },
            string.Empty, ModerationIds.VoteEvent(voteId, 1, "recipient"));
        var tally = await session.LoadAsync<ModerationTally>(ModerationIds.Tally(target))
                    ?? new ModerationTally { TargetId = target };
        tally.Up = tallyUp;
        await session.StoreAsync(tally, ModerationIds.Tally(target));
    }

    private async Task<bool> ExistsAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Advanced.ExistsAsync(id);
    }
}

public class MsSpikeContext : SparkContext
{
}
