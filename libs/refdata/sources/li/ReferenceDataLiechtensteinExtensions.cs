using MintPlayer.Spark.ReferenceData.Sources.Liechtenstein;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataLiechtensteinExtensions
{
    /// <summary>
    /// Liechtenstein (PRD §4.4): source <c>li-swisstopo-addresses</c> (swisstopo's address directory for Liechtenstein),
    /// scheduled when <c>Spark:ReferenceData:Sources:li-swisstopo-addresses:Enabled</c> is on, and its mapper with the
    /// shipped ledger. Attribution "©swisstopo" as for Switzerland.
    /// </summary>
    public static ReferenceDataBuilder AddLiechtenstein(this ReferenceDataBuilder builder)
        => builder
            .AddSource<LiechtensteinAddressesSource>()
            .AddMapper<LiechtensteinMapper>();
}
