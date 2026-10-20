using MintPlayer.Spark.ReferenceData.Sources.Poland;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataPolandExtensions
{
    /// <summary>
    /// Poland (PRD §4.4): sources <c>pl-teryt</c> (GUS TERYT TERC, SIMC and ULIC full files) and <c>pl-prg</c> (GUGiK PRG
    /// address points, for postcodes), scheduled when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the
    /// Polish mapper with its shipped ledger.
    /// </summary>
    public static ReferenceDataBuilder AddPoland(this ReferenceDataBuilder builder)
        => builder
            .AddSource<PolandTerytSource>()
            .AddSource<PolandPrgSource>()
            .AddMapper<PolandMapper>();
}
