using MintPlayer.Spark.ReferenceData.Sources.Finland;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataFinlandExtensions
{
    /// <summary>
    /// Finland (PRD §4.4): sources <c>fi-kunta</c> (Statistics Finland's yearly municipality and region lists since 1970)
    /// and <c>fi-ryhti</c> (SYKE Ryhti address points: streets and postcodes), scheduled when
    /// <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Finnish mapper with its shipped ledger.
    /// </summary>
    public static ReferenceDataBuilder AddFinland(this ReferenceDataBuilder builder)
        => builder
            .AddSource<FinlandClassificationSource>()
            .AddSource<FinlandRyhtiSource>()
            .AddMapper<FinlandMapper>();
}
