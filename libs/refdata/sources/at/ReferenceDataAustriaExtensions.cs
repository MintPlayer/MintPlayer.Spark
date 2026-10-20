using MintPlayer.Spark.ReferenceData.Sources.Austria;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataAustriaExtensions
{
    /// <summary>
    /// Austria (PRD §4.4): sources <c>at-bev-adressregister</c> (BEV's relational tables, twice a year),
    /// <c>at-statistik-regionen</c> (Bundesländer and districts) and <c>at-statistik-aenderungen</c> (the change file
    /// since 2002), scheduled when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Austrian mapper with
    /// its shipped ledger.
    /// </summary>
    public static ReferenceDataBuilder AddAustria(this ReferenceDataBuilder builder)
        => builder
            .AddSource<AustriaAddressRegisterSource>()
            .AddSource<AustriaRegionsSource>()
            .AddSource<AustriaChangesSource>()
            .AddMapper<AustriaMapper>();
}
