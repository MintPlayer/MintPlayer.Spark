using MintPlayer.Spark.ReferenceData.Sources;

namespace MintPlayer.Spark.ReferenceData.Build;

/// <summary>
/// Runs every registered mapper over source inputs and derives the result (PRD §4.5 steps 6 and 9). The same code
/// builds a full import, the committed basic seed set (from the adapters' fixtures, RD9) and the tests' datasets, so
/// all of them mint ids through the same ledgers.
/// </summary>
public sealed class RefDatasetBuilder(IEnumerable<IReferenceDataSource> sources, IEnumerable<IReferenceDataMapper> mappers)
{
    public IReadOnlyList<IReferenceDataSource> Sources { get; } = [.. sources.OrderBy(s => s.Id, StringComparer.Ordinal)];

    public IReadOnlyList<IReferenceDataMapper> Mappers { get; } = [.. mappers.OrderBy(m => m.Order).ThenBy(m => string.Join(",", m.Countries), StringComparer.Ordinal)];

    /// <summary>Every adapter's real fixture, by source id.</summary>
    public IReadOnlyDictionary<string, SourceInput> Fixtures()
        => Sources.ToDictionary(s => s.Id, s => s.Fixture(), StringComparer.Ordinal);

    /// <summary>
    /// Builds and derives. Throws <see cref="LedgerConflictException"/> when the ledger refused an id: nothing of the
    /// build may then be written (fail closed, §4.3).
    /// </summary>
    /// <param name="localLedger">The instance's own ledger lines (units first seen here, Q-R5).</param>
    public async Task<RefDataBuild> BuildAsync(IReadOnlyDictionary<string, SourceInput> inputs, IEnumerable<LedgerEntry>? localLedger, CancellationToken cancellationToken)
    {
        RefSearchKeys.AssertNormalizationAvailable();
        var ledger = new RefLedger(Mappers.SelectMany(m => m.ShippedLedger()).Concat(localLedger ?? []));
        var build = new RefDataBuild(ledger);
        foreach (var mapper in Mappers.Where(m => m.IsCountryAdapter))
            build.AdapterCountries.UnionWith(mapper.Countries);
        foreach (var mapper in Mappers)
        {
            var mine = mapper.SourceIds
                .Where(inputs.ContainsKey)
                .ToDictionary(id => id, id => inputs[id], StringComparer.Ordinal);
            if (mine.Count == 0)
                continue;
            await mapper.MapAsync(build, mine, cancellationToken);
            foreach (var input in mine.Values)
                if (Sources.FirstOrDefault(s => s.Id == input.SourceId) is { } source)
                    build.AddSource(input, source.Licence);
        }

        if (ledger.Conflicts.Count > 0)
            throw new LedgerConflictException(ledger.Conflicts);
        RefDerivation.Derive(build);
        return build;
    }

    /// <summary>The build of every adapter's fixture: the basic seed set.</summary>
    public Task<RefDataBuild> BuildFixturesAsync(CancellationToken cancellationToken) => BuildAsync(Fixtures(), null, cancellationToken);
}
