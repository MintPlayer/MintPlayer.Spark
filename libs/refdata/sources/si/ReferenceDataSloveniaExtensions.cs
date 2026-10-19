using MintPlayer.Spark.ReferenceData.Sources.Slovenia;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataSloveniaExtensions
{
    /// <summary>
    /// Slovenia (PRD §4.4): sources <c>si-rpe</c> (Register of Spatial Units) and <c>si-rn</c> (Register of Addresses),
    /// both through GURS's WFS, scheduled when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Slovenian
    /// mapper with its shipped ledger.
    /// </summary>
    public static ReferenceDataBuilder AddSlovenia(this ReferenceDataBuilder builder)
        => builder
            .AddSource<SloveniaUnitsSource>()
            .AddSource<SloveniaAddressesSource>()
            .AddMapper<SloveniaMapper>();
}
