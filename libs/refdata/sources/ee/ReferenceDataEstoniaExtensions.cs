using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.ReferenceData.Sources.Estonia;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataEstoniaExtensions
{
    /// <summary>
    /// Estonia (PRD §4.4): sources <c>ee-ehak</c> (EHAK settlement units with their municipality and county) and
    /// <c>ee-ads</c> (the ADS address extract nr 9 with history; its URL from <c>Spark:ReferenceData:Sources:ee-ads:Url</c>),
    /// scheduled when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the Estonian mapper with its shipped ledger.
    /// ⚠️ ADS limits downloads from foreign IP addresses: run the import where they are allowed.
    /// </summary>
    public static ReferenceDataBuilder AddEstonia(this ReferenceDataBuilder builder)
    {
        builder.AddSource<EstoniaEhakSource>().AddSource<EstoniaAdsSource>().AddMapper<EstoniaMapper>();
        // AddSource constructs the source itself; the extract URL is read from configuration when the import runs.
        var configuration = builder.Spark.Configuration;
        if (builder.Spark.Services.LastOrDefault(d => d.ServiceType == typeof(EstoniaAdsSource))?.ImplementationInstance is EstoniaAdsSource ads)
            ads.Url = () => configuration?[$"{SparkReferenceDataOptions.SectionName}:Sources:{EstoniaAdsSource.SourceId}:Url"] is { Length: > 0 } url ? url : null;
        return builder;
    }
}
