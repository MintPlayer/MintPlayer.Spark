using MintPlayer.Spark.ReferenceData.Sources.France;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataFranceExtensions
{
    /// <summary>
    /// France (PRD §4.4): sources <c>fr-cog</c> (INSEE COG, communes since 1943 with their mouvements), <c>fr-ban</c>
    /// (Base Adresse Nationale files per département) and <c>fr-laposte</c> (base officielle des codes postaux),
    /// scheduled when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the French mapper with its shipped
    /// ledger. Replaces the global baseline's FR profile (VAT territories included) and first-level units.
    /// </summary>
    public static ReferenceDataBuilder AddFrance(this ReferenceDataBuilder builder)
        => builder
            .AddSource<FranceCogSource>()
            .AddSource<FranceBanSource>()
            .AddSource<FranceLaPosteSource>()
            .AddMapper<FranceMapper>();
}
