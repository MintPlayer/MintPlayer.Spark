using System.Collections.Concurrent;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// One versioning run per repository at a time (#153 M6). Two publish workflows of one repository would otherwise plan
/// from the same tags and race to write the same versions; serialized, the second plans after the first's tags exist.
/// </summary>
/// <remarks>
/// In-process, because the app runs as one instance (as the merge queue's lanes assume). The git writer serializes its own
/// clones per repository too, so a second instance would cost a wait there, never a wrong tag: the tag writer refuses an
/// existing tag that points elsewhere.
/// </remarks>
public sealed class VersioningRunLocks
{
    /// <summary>How long a run waits behind another before the caller is told to retry (503).</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);

    /// <summary>The held lock, or null when <see cref="MaxWait"/> passed first.</summary>
    public async Task<IDisposable?> TryEnterAsync(string repositoryId, CancellationToken cancellationToken)
    {
        var gate = locks.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));
        return await gate.WaitAsync(MaxWait, cancellationToken) ? new Release(gate) : null;
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }
}
