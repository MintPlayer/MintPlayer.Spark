using MintPlayer.Spark.ReferenceData.Sources.Netherlands;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataNetherlandsExtensions
{
    /// <summary>
    /// The Netherlands (PRD §4.4): sources <c>nl-bag</c> (the free monthly LVBAG extract), <c>nl-cbs-gemeenten</c> and
    /// <c>nl-cbs-herindelingen</c>, scheduled when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Dutch
    /// mapper with its shipped ledger. Replaces the global baseline's NL profile and first-level units.
    /// </summary>
    public static ReferenceDataBuilder AddNetherlands(this ReferenceDataBuilder builder)
        => builder
            .AddSource<NetherlandsCbsGemeentenSource>()
            .AddSource<NetherlandsHerindelingenSource>()
            .AddSource<NetherlandsBagSource>()
            .AddMapper<NetherlandsMapper>();
}
