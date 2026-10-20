using MintPlayer.Spark.ReferenceData.Sources.Spain;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataSpainExtensions
{
    /// <summary>
    /// Spain (PRD §4.4): sources <c>es-ine</c> (municipality and province codes), <c>es-callejero</c> (entities and
    /// streets), <c>es-callejero-var</c> (street variations) and <c>es-cartociudad</c> (postcodes; no automatic change
    /// check, see its README), scheduled when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Spanish
    /// mapper with its shipped ledger.
    /// </summary>
    public static ReferenceDataBuilder AddSpain(this ReferenceDataBuilder builder)
        => builder
            .AddSource<SpainIneCodesSource>()
            .AddSource<SpainCallejeroSource>()
            .AddSource<SpainCallejeroVariationsSource>()
            .AddSource<SpainCartoCiudadSource>()
            .AddMapper<SpainMapper>();
}
