using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.ReferenceData.Sources.Denmark;

namespace MintPlayer.Spark.ReferenceData;

public static class ReferenceDataDenmarkExtensions
{
    /// <summary>
    /// Denmark (PRD §4.4): sources <c>dk-dagi</c> (regions, municipalities) and <c>dk-dar</c> (roads, postcodes) from
    /// Datafordeleren's file downloads, scheduled when <c>Spark:ReferenceData:Sources:{id}:Enabled</c> is on, and the
    /// Danish mapper with its shipped ledger. An API key is required from 2027-01-15: put it in user-secrets as
    /// <c>Spark:ReferenceData:Sources:dk-dar:ApiKey</c> (and <c>dk-dagi:ApiKey</c>, else the DAR key is used). It is read
    /// when a download runs and sent as a header only.
    /// </summary>
    public static ReferenceDataBuilder AddDenmark(this ReferenceDataBuilder builder)
    {
        builder.AddSource<DenmarkDagiSource>().AddSource<DenmarkDarSource>().AddMapper<DenmarkMapper>();
        var configuration = builder.Spark.Configuration;
        string? Key(string id) => configuration?[$"{SparkReferenceDataOptions.SectionName}:Sources:{id}:ApiKey"] is { Length: > 0 } key ? key : null;
        foreach (var descriptor in builder.Spark.Services.Where(d => d.ServiceType == typeof(DenmarkDarSource) || d.ServiceType == typeof(DenmarkDagiSource)).ToList())
            if (descriptor.ImplementationInstance is DatafordelerSource source)
                source.ApiKey = () => Key(source.Id) ?? Key(DenmarkDarSource.SourceId);
        return builder;
    }
}
