using MintPlayer.Spark.ReferenceData.Sources.Czechia;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataCzechiaExtensions
{
    /// <summary>
    /// Czechia (PRD §4.4): sources <c>cz-ruian-units</c> and <c>cz-ruian-addresses</c> (ČÚZK RÚIAN, monthly), scheduled
    /// when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Czech mapper with its shipped ledger.
    /// </summary>
    public static ReferenceDataBuilder AddCzechia(this ReferenceDataBuilder builder)
        => builder
            .AddSource<CzechiaUnitsSource>()
            .AddSource<CzechiaAddressesSource>()
            .AddMapper<CzechiaMapper>();
}
