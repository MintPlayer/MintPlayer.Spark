using MintPlayer.Spark.ReferenceData.Sources.Australia;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataAustraliaExtensions
{
    /// <summary>
    /// Australia (PRD §4.4): source <c>au-gnaf</c> (Geoscape G-NAF, quarterly), scheduled when
    /// <c>Spark:ReferenceData:Sources:au-gnaf:Enabled</c> is on, and the Australian mapper with its shipped ledger. The
    /// G-NAF EULA's mail-use restriction travels in the source's attribution and must be in the host's API terms (PRD §4.12).
    /// </summary>
    public static ReferenceDataBuilder AddAustralia(this ReferenceDataBuilder builder)
        => builder
            .AddSource<AustraliaGnafSource>()
            .AddMapper<AustraliaMapper>();
}
