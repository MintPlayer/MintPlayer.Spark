using MintPlayer.Spark.ReferenceData.Sources.Luxembourg;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataLuxembourgExtensions
{
    /// <summary>
    /// Luxembourg (PRD §4.4): source <c>lu-caclr</c> (the national register of localities and streets), scheduled when
    /// <c>Spark:ReferenceData:Sources:lu-caclr:Enabled</c> is on, and the Luxembourg mapper with its shipped ledger.
    /// Replaces the global baseline's LU profile and first-level units.
    /// </summary>
    public static ReferenceDataBuilder AddLuxembourg(this ReferenceDataBuilder builder)
        => builder
            .AddSource<LuxembourgCaclrSource>()
            .AddMapper<LuxembourgMapper>();
}
