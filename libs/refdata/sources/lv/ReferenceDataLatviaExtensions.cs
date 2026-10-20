using MintPlayer.Spark.ReferenceData.Sources.Latvia;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataLatviaExtensions
{
    /// <summary>
    /// Latvia (PRD §4.4): source <c>lv-var</c> (the VZD State Address Register on data.gov.lv, daily), scheduled when
    /// <c>Spark:ReferenceData:Sources:lv-var:Enabled</c> is on, and the Latvian mapper with its shipped ledger.
    /// </summary>
    public static ReferenceDataBuilder AddLatvia(this ReferenceDataBuilder builder)
        => builder
            .AddSource<LatviaAddressRegisterSource>()
            .AddMapper<LatviaMapper>();
}
