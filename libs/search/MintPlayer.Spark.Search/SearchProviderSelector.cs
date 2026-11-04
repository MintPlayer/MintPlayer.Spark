using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions.Search;

namespace MintPlayer.Spark.Search;

/// <summary>
/// Which provider answers (<c>docs/spark_search_PRD.md</c> D6): the engine an engine package registered, else the
/// RavenDB one. While the engine fails, a circuit opens and the RavenDB provider answers for <see cref="OpenFor"/>, so a
/// stopped engine container costs one failed call per period instead of a timeout per keystroke. The RavenDB index is
/// always deployed, so the fallback is always current.
/// </summary>
internal sealed class SearchProviderSelector(IServiceProvider services, SparkSearchOptions options, SearchCircuit circuit, ILogger<SearchProviderSelector> logger)
    : ISparkSearchProvider
{
    /// <summary>How long the RavenDB provider answers after the engine failed.</summary>
    internal static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(30);

    public Task<IReadOnlyList<SparkSearchCandidate>> SearchAsync(SparkSearchRequest request, CancellationToken cancellationToken)
        => CallAsync(p => p.SearchAsync(request, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<SparkSearchCandidate>> FindByLabelAsync(string label, IReadOnlyCollection<string> entityTypes, CancellationToken cancellationToken)
        => CallAsync(p => p.FindByLabelAsync(label, entityTypes, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<SparkSearchCandidate>> CallAsync(Func<ISparkSearchProvider, Task<IReadOnlyList<SparkSearchCandidate>>> call, CancellationToken cancellationToken)
    {
        var fallback = services.GetRequiredService<RavenSearchProvider>();
        if (options.EngineProviderType is not { } engineType || circuit.IsOpen)
            return await call(fallback);

        try
        {
            return await call((ISparkSearchProvider)services.GetRequiredService(engineType));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (circuit.Trip(OpenFor))
                logger.LogWarning(ex, "The search engine failed; the RavenDB index answers searches for the next {Seconds} s.", OpenFor.TotalSeconds);
            return await call(fallback);
        }
    }
}

/// <summary>The engine circuit's state, one per application.</summary>
internal sealed class SearchCircuit(TimeProvider time)
{
    private long openUntilTicks;

    public bool IsOpen => time.GetUtcNow().UtcTicks < Interlocked.Read(ref openUntilTicks);

    /// <summary>Opens the circuit; true when it was closed, so only the first failure of a period is logged.</summary>
    public bool Trip(TimeSpan duration)
    {
        var wasOpen = IsOpen;
        Interlocked.Exchange(ref openUntilTicks, time.GetUtcNow().Add(duration).UtcTicks);
        return !wasOpen;
    }
}
