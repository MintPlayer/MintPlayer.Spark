using MintPlayer.Spark.ReferenceData.Sources.Belgium;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Best;
using MintPlayer.Spark.ReferenceData.Sources.Belgium.Flemish;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataBelgiumExtensions
{
    /// <summary>
    /// Belgium in depth (PRD §4.4): sources <c>be-refnis</c>, <c>be-nis6</c>, <c>be-postalstreets</c>, <c>be-best</c> (the
    /// BeST bundle's non-address entries, by range request), <c>be-flemish-diff</c> (the 2025 merger diff files, pinned)
    /// and <c>be-flemish-api</c> (the Flemish Basisregisters API), scheduled when
    /// <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Belgian mapper with its shipped ledger.
    /// </summary>
    public static ReferenceDataBuilder AddBelgium(this ReferenceDataBuilder builder)
        => builder
            .AddSource<BelgiumRefnisSource>()
            .AddSource<BelgiumNis6Source>()
            .AddSource<BelgiumPostalStreetsSource>()
            .AddSource<BelgiumBestSource>()
            .AddSource<FlemishDiffSource>()
            .AddSource<FlemishApiSource>()
            .AddMapper<BelgiumMapper>();
}
