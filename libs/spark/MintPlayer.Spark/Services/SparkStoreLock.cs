using System.Globalization;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Services;

/// <summary>
/// <see cref="ISparkStoreLock"/> over <see cref="ISparkAtomicValues"/> (#536 S6/S15), for every provider. The value is
/// <c>{holder}|{expires, round-trip UTC}</c>; the holder renews it by compare-and-swap every third of the time to live.
/// </summary>
/// <remarks>
/// A bare round-trip timestamp is a lease too, of an unknown holder: the migration runner's lock was one before #536, so
/// a deploy that meets a lock the previous version left behind respects it until it expires. A value that is neither is
/// taken over: a lock nobody can read would otherwise block its key for good.
/// </remarks>
internal sealed partial class SparkStoreLock : ISparkStoreLock
{
    [Inject] private readonly ISparkAtomicValues values;
    [Inject] private readonly TimeProvider time;

    public async Task<ISparkLease?> TryAcquireAsync(string key, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);

        var holder = Guid.NewGuid().ToString("N");
        var current = await values.GetAsync(key, cancellationToken);
        if (current is not null && ExpiresAt(current.Value) is { } expires && expires > time.GetUtcNow())
            return null;

        var taken = await values.TryPutAsync(key, Format(holder, time.GetUtcNow() + timeToLive), current?.Version ?? 0, cancellationToken);
        return taken is null ? null : new Lease(values, time, holder, timeToLive, taken);
    }

    internal static string Format(string holder, DateTimeOffset expires)
        => $"{holder}|{expires.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}";

    /// <summary>When the lease in <paramref name="value"/> expires; <see langword="null"/> when it can't be read.</summary>
    internal static DateTimeOffset? ExpiresAt(string value)
    {
        var separator = value.LastIndexOf('|');
        var text = separator < 0 ? value : value[(separator + 1)..];
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires)
            ? expires
            : null;
    }

    private sealed class Lease : ISparkLease
    {
        private readonly ISparkAtomicValues values;
        private readonly TimeProvider time;
        private readonly string holder;
        private readonly TimeSpan timeToLive;
        private readonly CancellationTokenSource lost = new();
        private readonly CancellationTokenSource stop = new();
        private readonly Task renewal;
        private SparkAtomicValue current;

        public Lease(ISparkAtomicValues values, TimeProvider time, string holder, TimeSpan timeToLive, SparkAtomicValue taken)
        {
            this.values = values;
            this.time = time;
            this.holder = holder;
            this.timeToLive = timeToLive;
            current = taken;
            renewal = Task.Run(RenewAsync);
        }

        public string Key => current.Key;

        public CancellationToken Lost => lost.Token;

        private async Task RenewAsync()
        {
            var interval = timeToLive / 3;
            var expires = time.GetUtcNow() + timeToLive;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, time, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    var next = time.GetUtcNow() + timeToLive;
                    var renewed = await values.TryPutAsync(current.Key, Format(holder, next), current.Version, stop.Token);
                    if (renewed is null)
                    {
                        lost.Cancel();   // somebody else holds it now
                        return;
                    }

                    current = renewed;
                    expires = next;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception) when (time.GetUtcNow() < expires)
                {
                    // The store didn't answer: try again next round while the lease still holds.
                }
                catch (Exception)
                {
                    lost.Cancel();   // it expired while the store was unreachable
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (stop.IsCancellationRequested)
                return;

            stop.Cancel();
            await renewal;
            // After a loss the version is somebody else's, and the delete fails harmlessly.
            if (!lost.IsCancellationRequested)
                await values.TryDeleteAsync(current.Key, current.Version);

            // Lost stays readable after disposal; a source without a timer holds nothing to release.
            stop.Dispose();
        }
    }
}
