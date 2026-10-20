using MintPlayer.Spark.ReferenceData.Sources.Switzerland;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataSwitzerlandExtensions
{
    /// <summary>
    /// Switzerland (PRD §4.4): sources <c>ch-bfs-communes</c> (the BFS commune register and its mutations) and
    /// <c>ch-swisstopo-streets</c> (the official street directory), scheduled when
    /// <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Swiss mapper with its shipped ledger.
    /// "©swisstopo" must appear in every response carrying CH data and in the service metadata.
    /// </summary>
    public static ReferenceDataBuilder AddSwitzerland(this ReferenceDataBuilder builder)
        => builder
            .AddSource<SwitzerlandCommunesSource>()
            .AddSource<SwitzerlandStreetsSource>()
            .AddMapper<SwitzerlandMapper>();
}
