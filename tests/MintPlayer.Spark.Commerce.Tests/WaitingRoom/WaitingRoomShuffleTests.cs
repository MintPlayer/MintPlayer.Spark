using MintPlayer.Spark.WaitingRoom.Services;

namespace MintPlayer.Spark.Commerce.Tests.WaitingRoom;

/// <summary>
/// <c>spark-shuffle-v1</c> (§4.12, S12 item S10 reduced per round 4: no 500,000-entry load run, the statistics at CI size):
/// deterministic from the seed and the entry set, a permutation, independent of arrival order, and uniform.
/// </summary>
public class WaitingRoomShuffleTests
{
    private static readonly string Seed = new('a', 64);

    [Fact]
    public void The_commitment_is_the_sha256_of_the_seed_bytes()
    {
        var seed = WaitingRoomShuffle.NewSeed();
        seed.Length.Should().Be(64);
        WaitingRoomShuffle.Commit(seed).Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(seed))));
        WaitingRoomShuffle.NewSeed().Should().NotBe(seed);
    }

    [Fact]
    public void The_order_is_a_permutation_fixed_by_the_seed_and_the_set_not_by_arrival()
    {
        var ids = Enumerable.Range(0, 500).Select(i => $"WaitingRoomEntries/{i:x8}").ToList();
        var arrived = ids.AsEnumerable().Reverse().ToList();

        var a = WaitingRoomShuffle.Order(ids, Seed, out var hashA);
        var b = WaitingRoomShuffle.Order(arrived, Seed, out var hashB);

        a.Should().Equal(b);
        hashA.Should().Be(hashB);
        a.Should().OnlyHaveUniqueItems();
        a.Should().HaveCount(500);
        a.SequenceEqual(ids.OrderBy(i => i, StringComparer.Ordinal)).Should().BeFalse();

        WaitingRoomShuffle.Order(ids, new string('b', 64), out _).SequenceEqual(a).Should().BeFalse();
        WaitingRoomShuffle.Order(ids.Skip(1), Seed, out var hashC).SequenceEqual(a.Where(i => i != ids[0])).Should().BeFalse();
        hashC.Should().NotBe(hashA);
    }

    [Fact]
    public void Every_position_is_equally_likely()
    {
        // 5 entries, 5,000 seeds: each entry should be first about 1,000 times. A chi-square over 4 degrees of freedom
        // stays under 18.47 with probability 0.999.
        var ids = new[] { "e/1", "e/2", "e/3", "e/4", "e/5" };
        var firsts = new Dictionary<string, int>();
        for (var i = 0; i < 5_000; i++)
        {
            var seed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i)));
            var first = WaitingRoomShuffle.Order(ids, seed, out _)[0];
            firsts[first] = firsts.GetValueOrDefault(first) + 1;
        }
        var chiSquare = ids.Sum(id => Math.Pow(firsts.GetValueOrDefault(id) - 1_000, 2) / 1_000);
        chiSquare.Should().BeLessThan(18.47);
    }

    [Fact]
    public void Rank_is_independent_of_arrival_time()
    {
        // S10's criterion at CI size: the Spearman correlation between arrival order and drawn rank is near zero.
        const int n = 2_000;
        var ids = Enumerable.Range(0, n).Select(i => $"WaitingRoomEntries/{i:x8}").ToList(); // index = arrival order
        var order = WaitingRoomShuffle.Order(ids, Seed, out _);
        var rankOf = order.Select((id, rank) => (id, rank)).ToDictionary(p => p.id, p => (double)p.rank);
        var sumSquares = ids.Select((id, arrival) => Math.Pow(arrival - rankOf[id], 2)).Sum();
        var spearman = 1 - 6 * sumSquares / (n * ((double)n * n - 1));
        Math.Abs(spearman).Should().BeLessThan(0.1);
    }
}
