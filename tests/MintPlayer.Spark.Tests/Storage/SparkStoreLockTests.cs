using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage.InMemory;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// Core's <see cref="ISparkStoreLock"/> over <see cref="ISparkAtomicValues"/> (#536 S6/S15): a lease that expires unless
/// renewed, taken over after its holder stops, and readable from the bare timestamp the migration runner wrote before.
/// </summary>
public class SparkStoreLockTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    private readonly InMemoryAtomicValues values = new();
    private readonly SparkStoreLock locks;

    public SparkStoreLockTests() => locks = new SparkStoreLock(values, TimeProvider.System);

    [Fact]
    public async Task A_free_key_is_taken_and_a_second_holder_is_refused()
    {
        await using var lease = await locks.TryAcquireAsync("lock/a", Ttl);

        lease.Should().NotBeNull();
        lease!.Key.Should().Be("lock/a");
        (await locks.TryAcquireAsync("lock/a", Ttl)).Should().BeNull();
        lease.Lost.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task Disposing_releases_the_lease()
    {
        var lease = await locks.TryAcquireAsync("lock/b", Ttl);
        await lease!.DisposeAsync();

        (await values.GetAsync("lock/b")).Should().BeNull();
        await using var again = await locks.TryAcquireAsync("lock/b", Ttl);
        again.Should().NotBeNull();
    }

    [Fact]
    public async Task An_expired_lease_is_taken_over()
    {
        await values.TryPutAsync("lock/c", SparkStoreLock.Format("someone-else", DateTimeOffset.UtcNow.AddMinutes(-1)), 0);

        await using var lease = await locks.TryAcquireAsync("lock/c", Ttl);

        lease.Should().NotBeNull();
    }

    [Fact]
    public async Task A_live_lease_of_another_holder_is_respected()
    {
        await values.TryPutAsync("lock/d", SparkStoreLock.Format("someone-else", DateTimeOffset.UtcNow.AddMinutes(5)), 0);

        (await locks.TryAcquireAsync("lock/d", Ttl)).Should().BeNull();
    }

    [Theory]
    [InlineData(-5, true)]
    [InlineData(20, false)]
    public async Task A_bare_timestamp_from_before_536_counts_as_a_lease(int minutesFromNow, bool free)
    {
        await values.TryPutAsync("lock/e", DateTime.UtcNow.AddMinutes(minutesFromNow).ToString("O"), 0);

        await using var lease = await locks.TryAcquireAsync("lock/e", Ttl);

        (lease is not null).Should().Be(free);
    }

    [Fact]
    public async Task An_unreadable_value_is_taken_over_rather_than_blocking_forever()
    {
        await values.TryPutAsync("lock/f", "garbage", 0);

        await using var lease = await locks.TryAcquireAsync("lock/f", Ttl);

        lease.Should().NotBeNull();
    }

    [Fact]
    public async Task A_held_lease_is_renewed_past_its_time_to_live()
    {
        var ttl = TimeSpan.FromMilliseconds(600);
        await using var lease = await locks.TryAcquireAsync("lock/g", ttl);
        var first = (await values.GetAsync("lock/g"))!;

        await Task.Delay(ttl * 3);

        (await values.GetAsync("lock/g"))!.Version.Should().NotBe(first.Version, "the holder renewed it");
        (await locks.TryAcquireAsync("lock/g", ttl)).Should().BeNull("a renewed lease has not expired");
        lease!.Lost.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task A_lease_overwritten_by_someone_else_is_lost_and_its_release_leaves_theirs()
    {
        var ttl = TimeSpan.FromMilliseconds(600);
        var lease = await locks.TryAcquireAsync("lock/h", ttl);
        var current = (await values.GetAsync("lock/h"))!;
        var theirs = SparkStoreLock.Format("someone-else", DateTimeOffset.UtcNow.AddMinutes(5));
        await values.TryPutAsync("lock/h", theirs, current.Version);

        // A failure bound: the renewal runs every 200 ms, so the loss shows within one round.
        await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(10)), Task.Delay(Timeout.Infinite, lease!.Lost)
            .ContinueWith(_ => { }, TaskScheduler.Default));
        await lease.DisposeAsync();

        lease.Lost.IsCancellationRequested.Should().BeTrue("the renewal found another holder");
        (await values.GetAsync("lock/h"))!.Value.Should().Be(theirs);
    }
}
