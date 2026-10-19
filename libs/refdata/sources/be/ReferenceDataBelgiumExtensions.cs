using MintPlayer.Spark.ReferenceData.Sources.Belgium;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataBelgiumExtensions
{
    /// <summary>
    /// Belgium in depth (PRD §4.4): sources <c>be-refnis</c>, <c>be-nis6</c> and <c>be-postalstreets</c>, scheduled when
    /// <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Belgian mapper with its shipped ledger.
    /// </summary>
    public static ReferenceDataBuilder AddBelgium(this ReferenceDataBuilder builder)
        => builder
            .AddSource<BelgiumRefnisSource>()
            .AddSource<BelgiumNis6Source>()
            .AddSource<BelgiumPostalStreetsSource>()
            .AddMapper<BelgiumMapper>();
}
