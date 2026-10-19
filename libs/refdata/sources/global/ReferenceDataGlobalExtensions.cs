using MintPlayer.Spark.ReferenceData.Sources.Global;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataGlobalExtensions
{
    /// <summary>
    /// The global baseline (PRD §4.4): <c>global-geonames</c>, <c>global-wikidata</c>, <c>global-cldr</c> (time-zone
    /// exemplar cities, §4.13) and <c>global-tzdata</c> with the baseline country profiles (DE; a country with its own
    /// adapter gets its profile from it), and the name sources <c>global-gns</c> (NGA GNS conventional names),
    /// <c>global-unlocode</c> (UN/LOCODE codes and conventional names) and <c>global-iana-subtags</c> (the IANA Language
    /// Subtag Registry, validating every tag). The name sources run after the country adapters, so they reach their places.
    /// </summary>
    public static ReferenceDataBuilder AddGlobalBaseline(this ReferenceDataBuilder builder)
        => builder
            .AddSource<GeoNamesSource>()
            .AddSource<WikidataSource>()
            .AddSource<CldrSource>()
            .AddSource<TzDataSource>()
            .AddSource<GnsSource>()
            .AddSource<UnLocodeSource>()
            .AddSource<IanaSubtagSource>()
            .AddMapper<GlobalMapper>()
            .AddMapper<GlobalNamesMapper>();
}
