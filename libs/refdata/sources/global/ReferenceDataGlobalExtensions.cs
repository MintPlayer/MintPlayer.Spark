using MintPlayer.Spark.ReferenceData.Sources.Global;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataGlobalExtensions
{
    /// <summary>
    /// The global baseline (PRD §4.4): <c>global-geonames</c>, <c>global-wikidata</c>, <c>global-cldr</c> (time-zone
    /// exemplar cities, §4.13) and <c>global-tzdata</c>, with the baseline country profiles of NL, FR, LU and DE.
    /// </summary>
    public static ReferenceDataBuilder AddGlobalBaseline(this ReferenceDataBuilder builder)
        => builder
            .AddSource<GeoNamesSource>()
            .AddSource<WikidataSource>()
            .AddSource<CldrSource>()
            .AddSource<TzDataSource>()
            .AddMapper<GlobalMapper>();
}
