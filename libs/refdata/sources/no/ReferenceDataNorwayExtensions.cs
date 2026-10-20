using MintPlayer.Spark.ReferenceData.Sources.Norway;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataNorwayExtensions
{
    /// <summary>
    /// Norway (PRD §4.4): sources <c>no-klass</c> (SSB municipalities and counties with their versions and changes),
    /// <c>no-adresser</c> (Kartverket address files) and <c>no-postnummer</c> (Bring), scheduled when
    /// <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Norwegian mapper with its shipped ledger.
    /// </summary>
    public static ReferenceDataBuilder AddNorway(this ReferenceDataBuilder builder)
        => builder
            .AddSource<NorwayKlassSource>()
            .AddSource<NorwayAddressSource>()
            .AddSource<NorwayPostcodeSource>()
            .AddMapper<NorwayMapper>();
}
